using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;
namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Trakt credential sync over the shared provider-credentials RPCs.
    /// Behavioral spec (do not diverge): js/core/profile/traktCredentialSyncService.js.
    /// </summary>
    public sealed class TraktCredentialSyncService
    {
        private const string Provider = "trakt";
        private const string PullRpc = "sync_pull_provider_credentials";
        private const string PushRpc = "sync_push_provider_credentials";
        private const string DeleteRpc = "sync_delete_provider_credentials";
        private const string AuthStoreKey = "traktAuthState";
        private const long TokenFallbackLifetimeSeconds = 86400;

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly IProfileIdProvider _profileIds;
        private readonly ISyncClientIdProvider _clientIds;
        private readonly Action<string> _logWarning;

        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        public TraktCredentialSyncService(
            SupabaseClient supabaseClient,
            AuthManager authManager,
            IKeyValueStore keyValueStore,
            IProfileIdProvider profileIdProvider = null,
            ISyncClientIdProvider clientIdProvider = null,
            Action<string> logWarning = null)
        {
            _supabase = supabaseClient ?? throw new ArgumentNullException(nameof(supabaseClient));
            _auth = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _profileIds = profileIdProvider ?? new DefaultProfileIdProvider();
            _clientIds = clientIdProvider != null
                ? clientIdProvider
                : new StoredSyncClientIdProvider(keyValueStore);
            _logWarning = logWarning;
        }

        // ------------------------------------------------------------------
        // Credential envelope helpers (exported for tests/integration)
        // ------------------------------------------------------------------

        public static long ResolveLifetimeSeconds(long? value)
        {
            var seconds = value ?? 0;
            if (seconds <= 0)
            {
                return TokenFallbackLifetimeSeconds;
            }
            return Math.Min(TokenFallbackLifetimeSeconds, seconds);
        }

        // js credentialJsonFromState: null unless access + refresh are present.
        public static Dictionary<string, object> BuildCredentialJson(
            IReadOnlyDictionary<string, JsonElement> state)
        {
            if (state == null)
            {
                return null;
            }
            var accessToken = (StringOrNull(state, "accessToken") ?? "").Trim();
            var refreshToken = (StringOrNull(state, "refreshToken") ?? "").Trim();
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
            {
                return null;
            }
            var createdAtRaw = NumberOr(state, "createdAt", 0);
            var now = NowUnixSeconds();
            var expiresInRaw = NumberOr(state, "expiresIn", TokenFallbackLifetimeSeconds);
            var credential = new Dictionary<string, object>
            {
                ["access_token"] = accessToken,
                ["refresh_token"] = refreshToken,
                ["token_type"] = OrDefault(StringOrNull(state, "tokenType"), "bearer"),
                ["created_at"] = createdAtRaw.HasValue && createdAtRaw.Value != 0
                    ? createdAtRaw.Value
                    : now,
                ["expires_in"] = ResolveLifetimeSeconds(expiresInRaw)
            };
            var username = StringOrNull(state, "username");
            var userSlug = StringOrNull(state, "userSlug");
            if (!string.IsNullOrEmpty(username))
            {
                credential["username"] = username;
            }
            if (!string.IsNullOrEmpty(userSlug))
            {
                credential["user_slug"] = userSlug;
            }
            return credential;
        }

        // js stateFromCredentialJson: accepts a JSON string or object with snake/camel keys.
        public static IReadOnlyDictionary<string, JsonElement> StateFromCredentialJson(
            JsonElement? credential)
        {
            if (credential == null)
            {
                return null;
            }
            var element = credential.Value;
            if (element.ValueKind == JsonValueKind.String)
            {
                try
                {
                    element = JsonDocument.Parse(element.GetString()).RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var accessToken = FirstString(element, "access_token", "accessToken").Trim();
            var refreshToken = FirstString(element, "refresh_token", "refreshToken").Trim();
            if (accessToken.Length == 0 || refreshToken.Length == 0)
            {
                return null;
            }

            var tokenType = FirstString(element, "token_type", "tokenType");
            if (tokenType.Length == 0)
            {
                tokenType = "bearer";
            }

            var now = NowUnixSeconds();
            var rawCreatedAt = NumberOrElement(element, "created_at", "createdAt");
            var rawExpiresIn = NumberOrElement(element, "expires_in", "expiresIn");

            return new Dictionary<string, JsonElement>
            {
                ["accessToken"] = Text(accessToken),
                ["refreshToken"] = Text(refreshToken),
                ["tokenType"] = Text(tokenType),
                ["createdAt"] =
                    Number(rawCreatedAt.HasValue && rawCreatedAt.Value != 0 ? rawCreatedAt.Value : now),
                ["expiresIn"] = Number(ResolveLifetimeSeconds(rawExpiresIn)),
                ["username"] = OptionalText(FirstString(element, "username")),
                ["userSlug"] = OptionalText(FirstString(element, "user_slug", "userSlug"))
            };
        }

        // js syncSignature.
        public static string SyncSignature(IReadOnlyDictionary<string, JsonElement> state)
        {
            var createdAt = NumberOr(state, "createdAt", null);
            var expiresIn = NumberOr(state, "expiresIn", null);
            var parts = new[]
            {
                StringOrNull(state, "accessToken") ?? "",
                StringOrNull(state, "refreshToken") ?? "",
                StringOrNull(state, "tokenType") ?? "",
                createdAt.HasValue && createdAt.Value != 0
                    ? createdAt.Value.ToString(CultureInfo.InvariantCulture)
                    : "",
                expiresIn.HasValue
                    ? ResolveLifetimeSeconds(expiresIn).ToString(CultureInfo.InvariantCulture)
                    : "",
                StringOrNull(state, "username") ?? "",
                StringOrNull(state, "userSlug") ?? ""
            };
            return string.Join("|", parts);
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        public async Task<bool> PushCurrentToRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            var resolvedProfileId = SyncProfileIds.Resolve(profileId, _profileIds);
            var state = await ReadStateAsync(resolvedProfileId).ConfigureAwait(false);
            return await PushStateToRemoteAsync(state, resolvedProfileId, ct).ConfigureAwait(false);
        }

        public Task<bool> PushStateToRemoteAsync(
            IReadOnlyDictionary<string, JsonElement> state = null,
            long? profileId = null,
            CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    var credentialJson = BuildCredentialJson(state);
                    if (credentialJson == null)
                    {
                        return false;
                    }
                    var resolvedProfileId = SyncProfileIds.Resolve(profileId, _profileIds);
                    await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = resolvedProfileId,
                        ["p_origin_client_id"] =
                            await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false),
                        ["p_credentials"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["provider"] = Provider,
                                ["credential_json"] = credentialJson
                            }
                        }
                    }, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Trakt credential sync push failed", error);
                    return false;
                }
            });
        }

        public Task<bool> PullFromRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    var resolvedProfileId = SyncProfileIds.Resolve(profileId, _profileIds);
                    var credentials = await _supabase.RpcAsync(PullRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = resolvedProfileId
                    }, true, ct).ConfigureAwait(false);

                    var traktCredential = FindProviderRow(credentials, Provider);
                    var remoteState = StateFromCredentialJson(UnwrapCredential(traktCredential));
                    if (remoteState == null)
                    {
                        return false;
                    }
                    var localState = await ReadStateAsync(resolvedProfileId).ConfigureAwait(false);
                    if (SyncSignature(localState) == SyncSignature(remoteState))
                    {
                        return false;
                    }
                    await SaveTokenAsync(resolvedProfileId, remoteState).ConfigureAwait(false);
                    await SaveUserAsync(
                        resolvedProfileId,
                        StringOrNull(remoteState, "username"),
                        StringOrNull(remoteState, "userSlug")).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Trakt credential sync pull failed", error);
                    return false;
                }
            });
        }

        public Task<bool> DeleteRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    await _supabase.RpcAsync(DeleteRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = SyncProfileIds.Resolve(profileId, _profileIds),
                        ["p_origin_client_id"] =
                            await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false),
                        ["p_provider"] = Provider
                    }, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Trakt credential sync delete failed", error);
                    return false;
                }
            });
        }

        // ------------------------------------------------------------------
        // traktAuthState envelope (js/data/local/traktAuthStore.js shape)
        // ------------------------------------------------------------------

        private Task<Dictionary<string, JsonElement>> ReadStateAsync(long profileId)
        {
            return RawProfileEnvelope.GetProfileAsync(_store, AuthStoreKey, Key(profileId));
        }

        // js TraktAuthStore.saveToken: token fields replaced, device-flow fields cleared.
        private async Task SaveTokenAsync(
            long profileId, IReadOnlyDictionary<string, JsonElement> remoteState)
        {
            var state = new Dictionary<string, JsonElement>(
                await ReadStateAsync(profileId).ConfigureAwait(false));
            foreach (var field in new[] { "accessToken", "refreshToken", "tokenType", "createdAt", "expiresIn" })
            {
                CopyField(remoteState, state, field);
            }
            foreach (var field in new[] { "deviceCode", "userCode", "verificationUrl", "expiresAt", "pollInterval" })
            {
                state[field] = NullElement();
            }
            await RawProfileEnvelope.SetProfileAsync(
                _store, AuthStoreKey, Key(profileId), state).ConfigureAwait(false);
        }

        // js TraktAuthStore.saveUser: merges identity fields only.
        private async Task SaveUserAsync(long profileId, string username, string userSlug)
        {
            var state = new Dictionary<string, JsonElement>(
                await ReadStateAsync(profileId).ConfigureAwait(false));
            state["username"] = OptionalText(username);
            state["userSlug"] = OptionalText(userSlug);
            await RawProfileEnvelope.SetProfileAsync(
                _store, AuthStoreKey, Key(profileId), state).ConfigureAwait(false);
        }

        private static void CopyField(
            IReadOnlyDictionary<string, JsonElement> source,
            IDictionary<string, JsonElement> target, string field)
        {
            JsonElement element;
            target[field] = source.TryGetValue(field, out element)
                ? element.Clone()
                : NullElement();
        }

        private static JsonElement NullElement()
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

        internal static JsonElement? FindProviderRow(JsonElement rows, string provider)
        {
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("provider", out var rowProvider) &&
                        rowProvider.ValueKind == JsonValueKind.String &&
                        string.Equals(rowProvider.GetString(), provider,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return row.Clone();
                    }
                }
            }
            return null;
        }

        // js row.credential_json || row.credentialJson — the row wrapper is stripped
        // before the credential itself is parsed.
        internal static JsonElement? UnwrapCredential(JsonElement? row)
        {
            if (row == null || row.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            JsonElement value;
            if (!row.Value.TryGetProperty("credential_json", out value) &&
                !row.Value.TryGetProperty("credentialJson", out value))
            {
                return null;
            }
            return value.Clone();
        }

        // js String(v || "") || null semantics for state fields.
        private static string StringOrNull(IReadOnlyDictionary<string, JsonElement> state, string key)
        {
            JsonElement element;
            if (state == null || !state.TryGetValue(key, out element))
            {
                return null;
            }
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    return text.Length == 0 ? null : text;
                case JsonValueKind.Number:
                    return element.GetRawText();
                default:
                    return null;
            }
        }

        private static long? NumberOr(
            IReadOnlyDictionary<string, JsonElement> state, string key, long? fallback)
        {
            JsonElement element;
            if (state == null || !state.TryGetValue(key, out element) ||
                element.ValueKind != JsonValueKind.Number)
            {
                return fallback;
            }
            return ElementToLong(element, fallback);
        }

        // js Number(credential.created_at || credential.createdAt): snake key first.
        private static long? NumberOrElement(JsonElement element, string snakeKey, string camelKey)
        {
            JsonElement match;
            if (!element.TryGetProperty(snakeKey, out match) &&
                !element.TryGetProperty(camelKey, out match))
            {
                return null;
            }
            if (match.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            return ElementToLong(match, null);
        }

        private static long? ElementToLong(JsonElement element, long? fallback)
        {
            if (element.TryGetInt64(out var value))
            {
                return value;
            }
            double parsed;
            if (double.TryParse(element.GetRawText(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed) &&
                !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            {
                return (long)Math.Truncate(parsed);
            }
            return fallback;
        }

        private static string FirstString(JsonElement element, params string[] keys)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return "";
            }
            foreach (var property in element.EnumerateObject())
            {
                if (keys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? "";
                }
            }
            return "";
        }

        private static string OrDefault(string value, string fallback)
        {
            var trimmed = (value ?? "").Trim();
            return trimmed.Length > 0 ? trimmed : fallback;
        }

        private static long NowUnixSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static JsonElement Text(string value)
        {
            return JsonSerializer.SerializeToElement(value ?? "");
        }

        private static JsonElement OptionalText(string value)
        {
            return string.IsNullOrEmpty(value) ? NullElement() : JsonSerializer.SerializeToElement(value);
        }

        private static JsonElement Number(long value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        private static string Key(long profileId)
        {
            return profileId.ToString(CultureInfo.InvariantCulture);
        }

        private Task<T> WithSyncLockAsync<T>(Func<Task<T>> task)
        {
            return ExecuteWithinLock(task);
        }

        private async Task<T> ExecuteWithinLock<T>(Func<Task<T>> task)
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await task().ConfigureAwait(false);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private void Warn(string message, Exception error)
        {
            if (_logWarning != null)
            {
                _logWarning(message + ": " + (error != null ? error.Message : ""));
            }
        }
    }
}
