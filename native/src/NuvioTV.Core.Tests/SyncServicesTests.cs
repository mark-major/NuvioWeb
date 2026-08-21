using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using NuvioTV.Core.Debrid;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Storage;
using NuvioTV.Core.Sync;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the sync services against their JS behavioral specs:
    /// providerCredentialSyncService.js / traktCredentialSyncService.js /
    /// simklCredentialSyncService.js / librarySyncService.js / pluginSyncService.js /
    /// collectionSyncService.js.
    /// </summary>
    [Collection("StaticConfig")]
    public class SyncServicesTests : IDisposable
    {
        private readonly MemoryKeyValueStore _store = new MemoryKeyValueStore();
        private RecordingHandler _handler;
        private HttpClient _httpClient;
        private AuthManager _auth;

        public SyncServicesTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["supabaseUrl"] = "https://primary.supabase.co",
                ["supabaseAnonKey"] = "anon-test-key"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            AppConfig.ResetForTests();
        }

        // -----------------------------------------------------------------
        // Fixture helpers
        // -----------------------------------------------------------------

        private SupabaseClient CreateClient()
        {
            _handler = new RecordingHandler();
            _httpClient = new HttpClient(_handler);
            return new SupabaseClient(
                _httpClient, new StubSessionTokenProvider("session-token", "refresh-token"));
        }

        private async Task<AuthManager> CreateAuthenticatedAuthAsync()
        {
            await SessionStore.SetAccessTokenAsync(
                _store, JwtGenerator.GenerateToken(expiresInSeconds: 3600));
            await SessionStore.SetRefreshTokenAsync(_store, "rt");
            var auth = new AuthManager(_httpClient, _store);
            await auth.InitializeAsync();
            Assert.Equal(AuthState.Authenticated, auth.State);
            return auth;
        }

        private static HttpResponseMessage JsonResponse(object payload,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        private HttpResponseMessage OwnerResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\"user-1\"", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage ErrorResponse(int status, string code)
        {
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, message = code }),
                    Encoding.UTF8, "application/json")
            };
        }

        private Task SetScopedRaw(string key, long profileId, object value)
        {
            var envelope = new Dictionary<string, object>
            {
                ["__profileScoped"] = true,
                ["version"] = 1,
                ["profiles"] = new Dictionary<string, object>
                {
                    [profileId.ToString()] = value
                }
            };
            return LocalStore.SetAsync(key, envelope, _store);
        }

        private Task SetRawEnvelope(string key, long profileId, object state)
        {
            var envelope = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["profiles"] = new Dictionary<string, object>
                {
                    [profileId.ToString()] = state
                }
            };
            return LocalStore.SetAsync(key, envelope, _store);
        }

        private async Task<JsonElement> GetScopedProfile(
            IKeyValueStore store, string key, long profileId)
        {
            var raw = await store.GetAsync(key);
            using (var doc = JsonDocument.Parse(raw ?? "{}"))
            {
                var root = doc.RootElement.Clone();
                JsonElement profiles;
                JsonElement value;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("profiles", out profiles) &&
                    profiles.TryGetProperty(profileId.ToString(), out value))
                {
                    return value.Clone();
                }
                return JsonDocument.Parse("null").RootElement.Clone();
            }
        }

        private async Task<JsonElement> RawEnvelopeProfile(string key)
        {
            var raw = await _store.GetAsync(key);
            using (var doc = JsonDocument.Parse(raw))
            {
                return doc.RootElement.Clone()
                    .GetProperty("profiles").GetProperty("1").Clone();
            }
        }

        private static string StringField(JsonElement element, string key)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            JsonElement value;
            return element.TryGetProperty(key, out value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return true;
                }
                await Task.Delay(50);
            }
            return predicate();
        }

        private static readonly FixedProfileIdProvider ProfileIds = new FixedProfileIdProvider();

        private sealed class FixedProfileIdProvider : IProfileIdProvider
        {
            public string GetActiveProfileId()
            {
                return "1";
            }
        }

        private sealed class FixedClientIdProvider : ISyncClientIdProvider
        {
            public Task<string> GetClientIdAsync(CancellationToken ct = default)
            {
                return Task.FromResult("test-client-id");
            }
        }

        private AddonRepository CreateAddonRepository()
        {
            return new AddonRepository(_store,
                manifestFetcher: (url, ct) => Task.FromResult<AddonManifest>(null));
        }

        // -----------------------------------------------------------------
        // ProviderCredentialSyncService
        // -----------------------------------------------------------------

        [Fact]
        public void BuildSnapshot_OrdersDebridMdbListAnimeSkip_AndTrims()
        {
            var debrid = new Dictionary<string, JsonElement>
            {
                ["torboxApiKey"] = JsonSerializer.SerializeToElement("tb-key"),
                ["realDebridApiKey"] = JsonSerializer.SerializeToElement("rd-key"),
                ["preferredResolverProviderId"] = JsonSerializer.SerializeToElement("torbox")
            };
            var mdbList = new Dictionary<string, JsonElement>
            {
                ["apiKey"] = JsonSerializer.SerializeToElement("  mdb  ")
            };

            var snapshot = ProviderCredentialSyncService.BuildSnapshot(2, debrid, mdbList, null);

            Assert.Equal(2, snapshot.ProfileId);
            var expectedProviders = DebridProviders.All()
                .Select(p => "debrid:" + p.Id).ToList();
            expectedProviders.Add("mdblist");
            expectedProviders.Add("animeskip");
            Assert.Equal(expectedProviders, snapshot.Values.Select(v => v.Provider).ToList());
            Assert.All(snapshot.Values.Take(expectedProviders.Count - 2),
                entry => Assert.Equal("api_key", entry.Field));
            Assert.Equal("client_id", snapshot.Values[snapshot.Values.Count - 1].Field);
            Assert.Equal("mdb", snapshot.Values[snapshot.Values.Count - 2].Value);
        }

        [Fact]
        public void BuildParams_MatchesJsWireFormatExactly()
        {
            var snapshot = new ProviderCredentialSnapshot
            {
                ProfileId = 3,
                Values = new List<ProviderCredentialEntry>
                {
                    new ProviderCredentialEntry
                        { Provider = "debrid:torbox", Field = "api_key", Value = " k1 " },
                    new ProviderCredentialEntry
                        { Provider = "mdblist", Field = "api_key", Value = "m1" },
                    new ProviderCredentialEntry
                        { Provider = "animeskip", Field = "client_id", Value = "" }
                }
            };

            var payload = ProviderCredentialSyncService.BuildParams(snapshot, "client-x");
            var json = JsonSerializer.Serialize(payload);

            Assert.Equal(
                "{\"p_profile_id\":3,\"p_origin_client_id\":\"client-x\"," +
                "\"p_credentials\":[" +
                "{\"provider\":\"debrid:torbox\",\"credential_json\":{\"api_key\":\"k1\"}}," +
                "{\"provider\":\"mdblist\",\"credential_json\":{\"api_key\":\"m1\"}}," +
                "{\"provider\":\"animeskip\",\"credential_json\":{\"client_id\":\"\"}}]}",
                json);
        }

        [Fact]
        public void MergeRows_AppliesRemoteStrings_ParsesJsonAndThrowsOnInvalid()
        {
            var snapshot = ProviderCredentialSyncService.BuildSnapshot(1);

            var invalidRows = JsonSerializer.SerializeToElement(new object[]
            {
                new { provider = "animeskip", credential_json = "{\"wrong_field\":\"x\"}" }
            });
            var thrown = Assert.Throws<ArgumentException>(() =>
                ProviderCredentialSyncService.MergeRows(snapshot, invalidRows));
            Assert.Contains("Invalid credential payload for animeskip", thrown.Message);

            var validRows = JsonSerializer.SerializeToElement(new object[]
            {
                new { provider = "DEBRID:TORBOX", credential_json = "{\"api_key\":\" remote \"}" },
                new { provider = "mdblist", credentialJson = new { api_key = "m-remote" } }
            });
            var merged = ProviderCredentialSyncService.MergeRows(snapshot, validRows);
            Assert.Equal("remote",
                merged.Values.First(v => v.Provider == "debrid:torbox").Value);
            Assert.Equal("m-remote",
                merged.Values.First(v => v.Provider == "mdblist").Value);
            // Unmatched providers keep local values.
            Assert.Equal("", merged.Values.First(v => v.Provider == "animeskip").Value);
        }

        [Fact]
        public async Task SyncFromRemote_Unauthenticated_ReturnsFalse_WithoutRequests()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store); // signed out

            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.False(await service.SyncFromRemoteAsync());
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task SyncFromRemote_SeedsThenPulls_AndAppliesChangedCredentials()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetScopedRaw("mdbListSettings", 1, new { apiKey = "old" });

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new { ok = true })); // seed
            _handler.Enqueue(JsonResponse(new[]
            {
                new { provider = "mdblist", credential_json = "{\"api_key\":\"new-key\"}" }
            }));

            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.SyncFromRemoteAsync());

            Assert.Equal(3, _handler.Requests.Count);
            Assert.Contains("/rest/v1/rpc/sync_seed_provider_credentials",
                _handler.Requests[1].RequestUri.ToString());
            Assert.Contains("/rest/v1/rpc/sync_pull_provider_credentials",
                _handler.Requests[2].RequestUri.ToString());
            Assert.Equal("{\"p_profile_id\":1}", _handler.RequestBodies[2]);

            var settings = await GetScopedProfile(_store, "mdbListSettings", 1);
            Assert.Equal("new-key", StringField(settings, "apiKey"));
            Assert.True(service.LastForegroundPullAtMs > 0);
        }

        [Fact]
        public async Task SyncFromRemote_PendingScope_PushesLocalFirst_ThenClearsPending()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await LocalStore.SetAsync(
                "providerCredentialSyncPendingProfiles",
                new Dictionary<string, long> { ["user-1:1"] = 12345L },
                _store);

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new { ok = true })); // push
            _handler.Enqueue(JsonResponse(new { ok = true })); // seed
            _handler.Enqueue(JsonResponse(Array.Empty<object>())); // pull

            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            // JS returns whether remote differed; identical rows → false even after push.
            Assert.False(await service.SyncFromRemoteAsync());

            Assert.Contains("sync_push_provider_credentials",
                _handler.Requests[1].RequestUri.ToString());
            Assert.Contains("sync_seed_provider_credentials",
                _handler.Requests[2].RequestUri.ToString());
            var pendingRaw = await _store.GetAsync("providerCredentialSyncPendingProfiles");
            var pending = JsonDocument.Parse(pendingRaw ?? "{}").RootElement.Clone();
            Assert.False(pending.TryGetProperty("user-1:1", out _));
        }

        [Fact]
        public async Task PushCurrentToRemote_GoldenBody_FromLocalSettings()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetScopedRaw("animeSkipSettings", 1, new { clientId = " anime-1 " });
            await SetScopedRaw("debridSettings", 1, new { torboxApiKey = "tb" });

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new { ok = true }));

            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.PushCurrentToRemoteAsync());

            var requestIndex = _handler.Requests.IndexOf(_handler.Requests.Single(r =>
                r.RequestUri.ToString().Contains("sync_push_provider_credentials")));
            Assert.Equal(HttpMethod.Post, _handler.Requests[requestIndex].Method);
            var expectedCredentials = DebridProviders.All().Select(provider =>
                "{\"provider\":\"debrid:" + provider.Id +
                "\",\"credential_json\":{\"api_key\":\"" +
                (provider.Id == "torbox" ? "tb" : "") + "\"}}");
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_origin_client_id\":\"test-client-id\"," +
                "\"p_credentials\":[" + string.Join(",", expectedCredentials) + "," +
                "{\"provider\":\"mdblist\",\"credential_json\":{\"api_key\":\"\"}}," +
                "{\"provider\":\"animeskip\",\"credential_json\":{\"client_id\":\"anime-1\"}}]}",
                _handler.RequestBodies[requestIndex]);
        }

        [Fact]
        public async Task RequestForegroundPull_GatingMatchesJs()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store); // signed out: always false
            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.False(service.RequestForegroundPull(force: true));
            Assert.False(service.QueuePush());
            service.CancelForegroundPull();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task RequestForegroundPull_Authenticated_GatesSecondCall()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            try
            {
                Assert.True(service.RequestForegroundPull());
                Assert.False(service.RequestForegroundPull()); // timer already scheduled
                service.CancelForegroundPull();
                Assert.True(service.RequestForegroundPull()); // cancelled → can reschedule
            }
            finally
            {
                service.CancelForegroundPull();
            }
        }

        [Fact]
        public async Task PullRpcError_KeepsLocalCredentials_ReturnsFalse()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetScopedRaw("mdbListSettings", 1, new { apiKey = "keep-me" });

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new { ok = true })); // seed
            _handler.Enqueue(ErrorResponse(404, "PGRST202")); // pull fails

            var service = new ProviderCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.False(await service.SyncFromRemoteAsync());
            var settings = await GetScopedProfile(_store, "mdbListSettings", 1);
            Assert.Equal("keep-me", StringField(settings, "apiKey"));
        }

        // -----------------------------------------------------------------
        // TraktCredentialSyncService
        // -----------------------------------------------------------------

        [Fact]
        public void Trakt_BuildCredentialJson_GoldenWithLifetimeClamp()
        {
            var state = new Dictionary<string, JsonElement>
            {
                ["accessToken"] = JsonSerializer.SerializeToElement(" at "),
                ["refreshToken"] = JsonSerializer.SerializeToElement("rt"),
                ["tokenType"] = JsonSerializer.SerializeToElement("bearer"),
                ["createdAt"] = JsonSerializer.SerializeToElement(1700000000),
                ["expiresIn"] = JsonSerializer.SerializeToElement(172800),
                ["username"] = JsonSerializer.SerializeToElement("u"),
                ["userSlug"] = JsonSerializer.SerializeToElement("s")
            };

            var credential = TraktCredentialSyncService.BuildCredentialJson(state);

            Assert.Equal("at", credential["access_token"]);
            Assert.Equal("rt", credential["refresh_token"]);
            Assert.Equal("bearer", credential["token_type"]);
            Assert.Equal(1700000000L, credential["created_at"]);
            Assert.Equal(86400L, credential["expires_in"]); // min(86400, 172800)
            Assert.Equal("u", credential["username"]);
            Assert.Equal("s", credential["user_slug"]);

            Assert.Null(TraktCredentialSyncService.BuildCredentialJson(null));
            Assert.Null(TraktCredentialSyncService.BuildCredentialJson(
                new Dictionary<string, JsonElement>
                {
                    ["accessToken"] = JsonSerializer.SerializeToElement("a")
                }));
        }

        [Fact]
        public async Task Trakt_PushStateToRemote_GoldenBody()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var state = new Dictionary<string, JsonElement>
            {
                ["accessToken"] = JsonSerializer.SerializeToElement("ta"),
                ["refreshToken"] = JsonSerializer.SerializeToElement("tr"),
                ["tokenType"] = JsonSerializer.SerializeToElement("bearer"),
                ["createdAt"] = JsonSerializer.SerializeToElement(1700000000),
                ["expiresIn"] = JsonSerializer.SerializeToElement(86400),
                ["username"] = JsonSerializer.SerializeToElement("un"),
                ["userSlug"] = JsonSerializer.SerializeToElement("us")
            };

            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new TraktCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.PushStateToRemoteAsync(state));

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_push_provider_credentials",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_origin_client_id\":\"test-client-id\"," +
                "\"p_credentials\":[{\"provider\":\"trakt\",\"credential_json\":{" +
                "\"access_token\":\"ta\",\"refresh_token\":\"tr\",\"token_type\":\"bearer\"," +
                "\"created_at\":1700000000,\"expires_in\":86400," +
                "\"username\":\"un\",\"user_slug\":\"us\"}}]}",
                _handler.RequestBodies[0]);
        }

        [Fact]
        public async Task Trakt_PushWithoutRefreshToken_IsSkipped()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var service = new TraktCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            var state = new Dictionary<string, JsonElement>
            {
                ["accessToken"] = JsonSerializer.SerializeToElement("ta")
            };

            Assert.False(await service.PushStateToRemoteAsync(state));
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task Trakt_PullFromRemote_AppliesToken_ClearsDeviceFlow_ThenNoOp()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetRawEnvelope("traktAuthState", 1, new
            {
                accessToken = "old",
                refreshToken = "old-r",
                deviceCode = "dc",
                userCode = "uc",
                verificationUrl = "https://trakt.tv/activate",
                expiresAt = 123L,
                pollInterval = 5
            });

            var credentialJson =
                "{\"access_token\":\"ta\",\"refresh_token\":\"tr\"," +
                "\"token_type\":\"bearer\",\"created_at\":1700000000,\"expires_in\":3600," +
                "\"username\":\"un\",\"user_slug\":\"us\"}";
            _handler.Enqueue(JsonResponse(new[]
            {
                new { provider = "trakt", credential_json = credentialJson }
            }));
            var service = new TraktCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.PullFromRemoteAsync());

            var state = await RawEnvelopeProfile("traktAuthState");
            Assert.Equal("ta", StringField(state, "accessToken"));
            Assert.Equal("un", StringField(state, "username"));
            Assert.Equal("us", StringField(state, "userSlug"));
            Assert.Equal(JsonValueKind.Null, state.GetProperty("deviceCode").ValueKind);
            Assert.Equal(JsonValueKind.Null, state.GetProperty("expiresAt").ValueKind);

            // Same remote again → signature equal → false.
            _handler.Enqueue(JsonResponse(new[]
            {
                new { provider = "trakt", credential_json = credentialJson }
            }));
            Assert.False(await service.PullFromRemoteAsync());
        }

        [Fact]
        public async Task Trakt_DeleteRemote_GoldenBody()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new TraktCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.DeleteRemoteAsync(4));

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_delete_provider_credentials",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":4,\"p_origin_client_id\":\"test-client-id\"," +
                "\"p_provider\":\"trakt\"}",
                _handler.RequestBodies[0]);
        }

        // -----------------------------------------------------------------
        // SimklCredentialSyncService
        // -----------------------------------------------------------------

        [Fact]
        public async Task Simkl_Push_IncludesIdentityOnlyWhenPresent_GoldenBody()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetRawEnvelope("simklAuthState", 1, new
            {
                accessToken = "sa",
                username = "su",
                accountId = 42
            });

            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new SimklCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.PushCurrentToRemoteAsync());

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_push_provider_credentials",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_origin_client_id\":\"test-client-id\"," +
                "\"p_credentials\":[{\"provider\":\"simkl\",\"credential_json\":{" +
                "\"access_token\":\"sa\",\"username\":\"su\",\"account_id\":42}}]}",
                _handler.RequestBodies[0]);
        }

        [Fact]
        public async Task Simkl_Pull_ParsesStringCredential_SavesIdentity_ResetsPinSession()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetRawEnvelope("simklAuthState", 1, new
            {
                accessToken = "old",
                userCode = "pin",
                verificationUrl = "https://simkl.com/pin",
                expiresAt = 999L,
                pollInterval = 5
            });

            _handler.Enqueue(JsonResponse(new[]
            {
                new
                {
                    provider = "SIMKL",
                    credential_json =
                        "{\"access_token\":\"na\",\"account_id\":7,\"username\":\"nu\"}"
                }
            }));
            var service = new SimklCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.True(await service.PullFromRemoteAsync());

            var state = await RawEnvelopeProfile("simklAuthState");
            Assert.Equal("na", StringField(state, "accessToken"));
            Assert.Equal("nu", StringField(state, "username"));
            Assert.Equal(7, state.GetProperty("accountId").GetInt64());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("userCode").ValueKind);
            Assert.Equal(5, state.GetProperty("pollInterval").GetInt64());

            // Identical remote → no change.
            _handler.Enqueue(JsonResponse(new[]
            {
                new { provider = "simkl", credential_json =
                    "{\"access_token\":\"na\",\"account_id\":7,\"username\":\"nu\"}" }
            }));
            Assert.False(await service.PullFromRemoteAsync());
        }

        [Fact]
        public async Task Simkl_DeleteRemote_Unauthenticated_Noop()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store);
            var service = new SimklCredentialSyncService(
                supabase, _auth, _store, ProfileIds, new FixedClientIdProvider());

            Assert.False(await service.DeleteRemoteAsync());
            Assert.Empty(_handler.Requests);
        }

        // -----------------------------------------------------------------
        // LibrarySyncService
        // -----------------------------------------------------------------

        [Fact]
        public async Task Library_Pull_AddonsTableWins_ReplacesOverridesAndStates()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();
            await LocalStore.SetAsync("installedAddonUrls",
                new[] { "https://stale.example/manifest.json" }, _store);
            await LocalStore.SetAsync("installedAddonDisplayNames",
                new Dictionary<string, object>
                {
                    ["https://stale.example/manifest.json"] = "Stale"
                }, _store);

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new object[]
            {
                new
                {
                    url = "https://a.example/manifest.json",
                    display_name = "A",
                    enabled = true
                },
                new
                {
                    url = "https://b.example/manifest.json",
                    name = "Bee",
                    enabled = false
                }
            }));

            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            var urls = await service.PullAsync();

            Assert.Equal(new[]
            {
                "https://a.example/manifest.json",
                "https://b.example/manifest.json"
            }, urls.ToArray());

            Assert.Contains("/rest/v1/addons?", _handler.Requests[1].RequestUri.ToString());
            Assert.Contains("order=sort_order.asc",
                _handler.Requests[1].RequestUri.ToString());

            var names = await GetScopedProfile(_store, "installedAddonDisplayNames", 1);
            // Storage keys are canonicalized (trailing /manifest.json stripped).
            Assert.Equal("A", StringField(names, "https://a.example"));
            Assert.Equal("Bee", StringField(names, "https://b.example"));
            Assert.False(names.TryGetProperty("https://stale.example/manifest.json", out _));

            var states = await GetScopedProfile(_store, "installedAddonEnabledStates", 1);
            Assert.True(states.GetProperty("https://a.example").GetBoolean());
            Assert.False(states.GetProperty("https://b.example").GetBoolean());

            var status = service.GetLastPullStatus();
            Assert.Equal("ok", status.State);
            Assert.Equal(2, status.Count);
        }

        [Fact]
        public async Task Library_Pull_FallsBackToTvAddons_OnPgrst205()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(ErrorResponse(404, "PGRST205"));
            _handler.Enqueue(JsonResponse(new[]
            {
                new { base_url = "https://legacy.example/manifest.json", position = 0 }
            }));

            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            var urls = await service.PullAsync();

            Assert.Equal(new[] { "https://legacy.example/manifest.json" }, urls.ToArray());
            Assert.Contains("/rest/v1/addons?", _handler.Requests[1].RequestUri.ToString());
            Assert.Contains("/rest/v1/tv_addons?", _handler.Requests[2].RequestUri.ToString());
            Assert.Contains("owner_id=eq.user-1", _handler.Requests[2].RequestUri.ToString());
            Assert.Contains("order=position.asc", _handler.Requests[2].RequestUri.ToString());
            Assert.Equal("ok", service.GetLastPullStatus().State);
        }

        [Fact]
        public async Task Library_Pull_RpcFallback_WhenBothTablesMissing()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(ErrorResponse(404, "PGRST202"));
            _handler.Enqueue(ErrorResponse(404, "PGRST205"));
            _handler.Enqueue(JsonResponse(new[]
            {
                new { url = "https://rpc.example/manifest.json", sort_order = 0 }
            }));

            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            var urls = await service.PullAsync();

            Assert.Equal(new[] { "https://rpc.example/manifest.json" }, urls.ToArray());
            Assert.Contains("/rest/v1/rpc/sync_pull_addons",
                _handler.Requests[3].RequestUri.ToString());
            Assert.Equal("{\"p_profile_id\":1}", _handler.RequestBodies[3]);
        }

        [Fact]
        public async Task Library_Pull_ReadError_KeepsLocalUrls_AndRecordsError()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();
            await LocalStore.SetAsync("installedAddonUrls",
                new[] { "https://local.example/manifest.json" }, _store);

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(ErrorResponse(500, "XX000"));
            _handler.Enqueue(ErrorResponse(500, "XX000"));

            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            var urls = await service.PullAsync();

            // Repo reads canonicalize the stored URL (manifest suffix stripped).
            Assert.Equal(new[] { "https://local.example" }, urls.ToArray());
            var status = service.GetLastPullStatus();
            Assert.Equal("error", status.State);
            Assert.Equal(1, status.Count);
        }

        [Fact]
        public async Task Library_Pull_SignedOut_ReturnsEmpty()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store);
            var repo = CreateAddonRepository();
            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            var urls = await service.PullAsync();

            Assert.Empty(urls);
            Assert.Equal("signed-out", service.GetLastPullStatus().State);
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task Library_Push_RpcGolden_NameOnlyWhenOverrideSet()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();
            await LocalStore.SetAsync("installedAddonUrls",
                new[] { "https://one.example/m.json", "https://two.example/m.json" }, _store);
            await LocalStore.SetAsync("installedAddonEnabledStates",
                new Dictionary<string, object> { ["https://one.example/m.json"] = false },
                _store);
            await LocalStore.SetAsync("installedAddonDisplayNames",
                new Dictionary<string, object> { ["https://two.example/m.json"] = "Two" },
                _store);

            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new LibrarySyncService(supabase, _auth, repo, _store, ProfileIds);

            Assert.True(await service.PushAsync());

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_push_addons",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_addons\":[" +
                "{\"url\":\"https://one.example/m.json\",\"sort_order\":0,\"enabled\":false}," +
                "{\"url\":\"https://two.example/m.json\",\"sort_order\":1," +
                "\"enabled\":true,\"name\":\"Two\"}]}",
                _handler.RequestBodies[0]);
        }

        [Fact]
        public async Task Library_Push_RpcFailure_FallsBackToAddonsTable_WithOnConflictRetry()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();
            await LocalStore.SetAsync("installedAddonUrls",
                new[] { "https://one.example/m.json" }, _store);

            var deletes = new List<Tuple<string, string>>();
            SupabaseTableDelete delete = (table, filter, useSession, ct) =>
            {
                deletes.Add(Tuple.Create(table, filter));
                return Task.CompletedTask;
            };

            _handler.Enqueue(ErrorResponse(404, "PGRST202")); // rpc missing
            _handler.Enqueue(OwnerResponse()); // legacy fallback needs the owner id
            _handler.Enqueue(ErrorResponse(400, "42P10")); // upsert with on_conflict
            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent)); // retry

            var service = new LibrarySyncService(
                supabase, _auth, repo, _store, ProfileIds, deleteAsync: delete);

            Assert.True(await service.PushAsync());

            var fallback = deletes.Single();
            Assert.Equal("addons", fallback.Item1);
            Assert.Equal("user_id=eq.user-1&profile_id=eq.1", fallback.Item2);
            Assert.Equal(4, _handler.Requests.Count);
            Assert.Contains("on_conflict=", _handler.Requests[2].RequestUri.ToString());
            Assert.DoesNotContain("on_conflict=", _handler.Requests[3].RequestUri.ToString());
        }

        [Fact]
        public async Task Library_Push_AddonsTableMissing_FallsBackToTvAddons()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            var repo = CreateAddonRepository();
            await LocalStore.SetAsync("installedAddonUrls",
                new[] { "https://one.example/m.json" }, _store);

            var deletes = new List<Tuple<string, string>>();
            SupabaseTableDelete delete = (table, filter, useSession, ct) =>
            {
                deletes.Add(Tuple.Create(table, filter));
                return Task.CompletedTask;
            };

            _handler.Enqueue(ErrorResponse(404, "PGRST202")); // rpc missing
            _handler.Enqueue(OwnerResponse()); // legacy fallback needs the owner id
            _handler.Enqueue(ErrorResponse(404, "PGRST205")); // addons table missing
            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent)); // tv upsert

            var service = new LibrarySyncService(
                supabase, _auth, repo, _store, ProfileIds, deleteAsync: delete);

            Assert.True(await service.PushAsync());

            Assert.Equal(2, deletes.Count);
            Assert.Equal("addons", deletes[0].Item1);
            Assert.Equal("tv_addons", deletes[1].Item1);
            Assert.Equal("owner_id=eq.user-1", deletes[1].Item2);

            var tvUpsert = _handler.Requests[3];
            Assert.Contains("/rest/v1/tv_addons", tvUpsert.RequestUri.ToString());
            Assert.Contains("on_conflict=owner_id%2Cbase_url",
                tvUpsert.RequestUri.ToString());
            Assert.Equal(
                "[{\"owner_id\":\"user-1\",\"base_url\":\"https://one.example/m.json\"," +
                "\"position\":0}]",
                _handler.RequestBodies[3]);
        }

        // -----------------------------------------------------------------
        // PluginSyncService
        // -----------------------------------------------------------------

        [Fact]
        public async Task Plugins_Pull_MergesRemoteOverLocal_AppendsLeftovers()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await LocalStore.SetAsync("pluginSources", new[]
            {
                new
                {
                    id = "p1",
                    name = "L",
                    urlTemplate = "https://l.example/{id}",
                    enabled = true
                },
                new
                {
                    id = "p2",
                    name = "L2",
                    urlTemplate = "https://only.local/x",
                    enabled = false
                }
            }, _store);

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(new object[]
            {
                new { url = "https://r.example/a", name = "R" },
                new { url_template = "https://l.example/{id}", enabled = false }
            }));

            var service = new PluginSyncService(supabase, _auth, _store, ProfileIds);
            var merged = await service.PullAsync();

            Assert.Equal(3, merged.Count);
            Assert.Equal("https://r.example/a", merged[0].UrlTemplate);
            Assert.Equal("R", merged[0].Name);
            Assert.StartsWith("plugin_1_", merged[0].Id);
            Assert.True(merged[0].Enabled);

            Assert.Equal("https://l.example/{id}", merged[1].UrlTemplate);
            Assert.False(merged[1].Enabled);
            Assert.Equal("Plugin 2", merged[1].Name);

            Assert.Equal("https://only.local/x", merged[2].UrlTemplate);
            Assert.Equal("p2", merged[2].Id);
            Assert.False(merged[2].Enabled);

            // Persisted locally.
            var stored = await service.ReadLocalSourcesAsync(CancellationToken.None);
            Assert.Equal(merged.Select(s => s.UrlTemplate), stored.Select(s => s.UrlTemplate));
        }

        [Fact]
        public async Task Plugins_Pull_RemoteEmptyKeepsLocal()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await LocalStore.SetAsync("pluginSources", new[]
            {
                new
                {
                    id = "p1",
                    name = "L",
                    urlTemplate = "https://l.example/{id}",
                    enabled = true
                }
            }, _store);

            _handler.Enqueue(OwnerResponse());
            _handler.Enqueue(JsonResponse(Array.Empty<object>()));

            var service = new PluginSyncService(supabase, _auth, _store, ProfileIds);
            var merged = await service.PullAsync();

            Assert.Single(merged);
            Assert.Equal("https://l.example/{id}", merged[0].UrlTemplate);
        }

        [Fact]
        public async Task Plugins_Pull_Unauthenticated_ReturnsEmpty()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store);
            var service = new PluginSyncService(supabase, _auth, _store, ProfileIds);

            Assert.Empty(await service.PullAsync());
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task Plugins_Push_RpcGolden()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await LocalStore.SetAsync("pluginSources", new[]
            {
                new
                {
                    id = "p1",
                    name = "",
                    urlTemplate = "https://l.example/{id}",
                    enabled = true
                }
            }, _store);

            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new PluginSyncService(supabase, _auth, _store, ProfileIds);

            Assert.True(await service.PushAsync());

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_push_plugins",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_plugins\":[" +
                "{\"url\":\"https://l.example/{id}\",\"name\":\"Custom Source\"," +
                "\"enabled\":true,\"sort_order\":0}]}",
                _handler.RequestBodies[0]);
        }

        [Fact]
        public async Task Plugins_Push_LegacyFallback_OnPgrst202()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await LocalStore.SetAsync("pluginSources", new[]
            {
                new
                {
                    id = "p1",
                    name = "L",
                    urlTemplate = "https://l.example/{id}",
                    enabled = true
                }
            }, _store);

            var deletes = new List<Tuple<string, string>>();
            SupabaseTableDelete delete = (table, filter, useSession, ct) =>
            {
                deletes.Add(Tuple.Create(table, filter));
                return Task.CompletedTask;
            };

            _handler.Enqueue(ErrorResponse(404, "PGRST202")); // rpc missing
            _handler.Enqueue(OwnerResponse()); // legacy fallback needs the owner id
            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent)); // upsert

            var service = new PluginSyncService(
                supabase, _auth, _store, ProfileIds, deleteAsync: delete);

            Assert.True(await service.PushAsync());

            var fallback = deletes.Single();
            Assert.Equal("plugins", fallback.Item1);
            Assert.Equal("user_id=eq.user-1&profile_id=eq.1", fallback.Item2);
            var upsert = _handler.Requests[2];
            Assert.Contains("/rest/v1/plugins", upsert.RequestUri.ToString());
            Assert.Contains("on_conflict=user_id%2Cprofile_id%2Curl",
                upsert.RequestUri.ToString());
        }

        [Fact]
        public async Task Plugins_Push_NonLegacyRpcError_ReturnsFalse()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();

            _handler.Enqueue(ErrorResponse(400, "22003"));
            var service = new PluginSyncService(supabase, _auth, _store, ProfileIds);

            Assert.False(await service.PushAsync());
            Assert.Single(_handler.Requests);
        }

        // -----------------------------------------------------------------
        // CollectionSyncService
        // -----------------------------------------------------------------

        [Fact]
        public async Task Collections_Push_RoundTripsStoredCollections_GoldenBody()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetScopedRaw("collectionsState", 1, new
            {
                collections = new object[]
                {
                    new { id = "c1", title = "One", folders = new object[0] }
                }
            });

            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new CollectionSyncService(supabase, _auth, _store, ProfileIds);

            Assert.True(await service.PushAsync());

            Assert.Single(_handler.Requests);
            Assert.Contains("/rest/v1/rpc/sync_push_collections",
                _handler.Requests[0].RequestUri.ToString());
            Assert.Equal(
                "{\"p_profile_id\":1,\"p_collections_json\":[" +
                "{\"id\":\"c1\",\"title\":\"One\",\"folders\":[]}]}",
                _handler.RequestBodies[0]);
        }

        [Fact]
        public async Task Collections_Pull_AppliesRemoteBlob_WhenDifferent()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            await SetScopedRaw("collectionsState", 1, new
            {
                collections = new object[] { new { id = "c1" } }
            });

            _handler.Enqueue(JsonResponse(new[]
            {
                new { collections_json = "[{\"id\":\"c2\",\"title\":\"Two\"}]" }
            }));
            var service = new CollectionSyncService(supabase, _auth, _store, ProfileIds);

            Assert.False(service.IsSyncingFromRemote());
            Assert.True(await service.PullAsync());
            Assert.False(service.IsSyncingFromRemote()); // guard released

            var payload = await GetScopedProfile(_store, "collectionsState", 1);
            Assert.Equal(JsonValueKind.Array,
                payload.GetProperty("collections").ValueKind);
            Assert.Equal("c2",
                payload.GetProperty("collections")[0].GetProperty("id").GetString());

            // Same payload again → stable-stringify equal → false.
            _handler.Enqueue(JsonResponse(new[]
            {
                new { collections_json = "[{\"id\":\"c2\",\"title\":\"Two\"}]" }
            }));
            Assert.False(await service.PullAsync());
        }

        [Fact]
        public void Collections_StableStringify_IgnoresKeyOrder()
        {
            var left = CollectionSyncService.StableStringify(
                JsonDocument.Parse("{\"b\":1,\"a\":\"x\"}").RootElement.Clone());
            var right = CollectionSyncService.StableStringify(
                JsonDocument.Parse("{\"a\":\"x\",\"b\":1}").RootElement.Clone());
            Assert.Equal(left, right);
        }

        [Fact]
        public async Task Collections_TriggerPush_DebouncesThenPushes()
        {
            var supabase = CreateClient();
            _auth = await CreateAuthenticatedAuthAsync();
            _handler.Enqueue(JsonResponse(new { ok = true }));
            var service = new CollectionSyncService(supabase, _auth, _store, ProfileIds);

            Assert.True(service.TriggerPush());
            Assert.True(service.IsPushQueued(1));
            Assert.True(service.TriggerPush()); // replaces the pending timer

            // Debounce is 500ms; allow the timer to fire and complete.
            Assert.True(await WaitUntilAsync(() => _handler.Requests.Count == 1));
            Assert.Contains("sync_push_collections",
                _handler.Requests[0].RequestUri.ToString());
            Assert.True(await WaitUntilAsync(() => !service.IsPushQueued(1)));
        }

        [Fact]
        public async Task Collections_Pull_Unauthenticated_ReturnsFalse()
        {
            var supabase = CreateClient();
            _auth = new AuthManager(_httpClient, _store);
            var service = new CollectionSyncService(supabase, _auth, _store, ProfileIds);

            Assert.False(await service.PullAsync());
            Assert.Empty(_handler.Requests);
        }

        // -----------------------------------------------------------------
        // Shared error classifier
        // -----------------------------------------------------------------

        [Theory]
        [InlineData(404, null, true)]
        [InlineData(400, "PGRST205", true)]
        [InlineData(400, "PGRST202", true)]
        [InlineData(400, "42P10", false)]
        [InlineData(500, null, false)]
        public void SyncErrorClassifier_MissingResourceDetection(int status, string code,
            bool expected)
        {
            var error = new NuvioHttpException(status, code, code ?? "boom");
            Assert.Equal(expected, SyncErrorClassifier.IsMissingResourceError(error));
            Assert.Equal(expected, SyncErrorClassifier.ShouldTryLegacyTable(error));
        }

        [Fact]
        public void SyncErrorClassifier_OnConflictConstraintDetection()
        {
            var byCode = new NuvioHttpException(400, "42P10", "error");
            Assert.True(SyncErrorClassifier.IsOnConflictConstraintError(byCode));
            var byMessage = new NuvioHttpException(400, null,
                "no unique or exclusion constraint matching the ON CONFLICT specification");
            Assert.True(SyncErrorClassifier.IsOnConflictConstraintError(byMessage));
            var other = new NuvioHttpException(400, null, "something else");
            Assert.False(SyncErrorClassifier.IsOnConflictConstraintError(other));
        }
    }
}
