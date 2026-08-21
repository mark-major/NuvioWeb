using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Debrid;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>One credential row of a provider credential snapshot.</summary>
    public sealed class ProviderCredentialEntry
    {
        public string Provider { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }
    }

    /// <summary>Ordered credential snapshot for one profile.</summary>
    public sealed class ProviderCredentialSnapshot
    {
        public long ProfileId { get; set; }
        public List<ProviderCredentialEntry> Values { get; set; }
    }

    /// <summary>
    /// Debrid/MDBList/AnimeSkip credential sync. Behavioral spec (do not diverge):
    /// js/core/profile/providerCredentialSyncService.js.
    /// </summary>
    public sealed class ProviderCredentialSyncService
    {
        private const string SeedRpc = "sync_seed_provider_credentials";
        private const string PushRpc = "sync_push_provider_credentials";
        private const string PullRpc = "sync_pull_provider_credentials";

        // js PENDING_KEY / PUSH_DEBOUNCE_MS / foreground constants.
        private const string PendingKey = "providerCredentialSyncPendingProfiles";
        public const int PushDebounceMs = 500;
        public const int ForegroundDelayMs = 2500;
        public const int ForegroundMinIntervalMs = 60000;

        private const string ApiKeyField = "api_key";
        private const string ClientIdField = "client_id";
        private const string MdbListProvider = "mdblist";
        private const string AnimeSkipProvider = "animeskip";

        private const string DebridSettingsKey = "debridSettings";
        private const string MdbListSettingsKey = "mdbListSettings";
        private const string AnimeSkipSettingsKey = "animeSkipSettings";

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly IProfileIdProvider _profileIds;
        private readonly ISyncClientIdProvider _clientIds;
        private readonly Action<string> _logWarning;

        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);
        private readonly object _timerGate = new object();
        private readonly Dictionary<string, Timer> _pushTimers =
            new Dictionary<string, Timer>();
        private Timer _foregroundPullTimer;
        private bool _foregroundPullInFlight;
        private long _lastForegroundPullAtMs;

        public ProviderCredentialSyncService(
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
        // Pure snapshot helpers (exported for tests/integration)
        // ------------------------------------------------------------------

        // js providerName: "debrid:" + trimmed lowercase id.
        private static string ProviderName(string providerId)
        {
            return "debrid:" + (providerId ?? "").Trim().ToLowerInvariant();
        }

        // js buildProviderCredentialSnapshot over already-loaded settings objects.
        public static ProviderCredentialSnapshot BuildSnapshot(
            long profileId,
            IReadOnlyDictionary<string, JsonElement> debridSettings = null,
            IReadOnlyDictionary<string, JsonElement> mdbListSettings = null,
            IReadOnlyDictionary<string, JsonElement> animeSkipSettings = null)
        {
            var values = new List<ProviderCredentialEntry>();
            foreach (var provider in DebridProviders.All())
            {
                values.Add(new ProviderCredentialEntry
                {
                    Provider = ProviderName(provider.Id),
                    Field = ApiKeyField,
                    Value = DebridProviders.ApiKeyFor(ToStringMap(debridSettings), provider.Id)
                });
            }
            values.Add(new ProviderCredentialEntry
            {
                Provider = MdbListProvider,
                Field = ApiKeyField,
                Value = ScopedJsonStore.GetString(mdbListSettings, "apiKey").Trim()
            });
            values.Add(new ProviderCredentialEntry
            {
                Provider = AnimeSkipProvider,
                Field = ClientIdField,
                Value = ScopedJsonStore.GetString(animeSkipSettings, "clientId").Trim()
            });
            return new ProviderCredentialSnapshot
            {
                ProfileId = profileId,
                Values = values
            };
        }

        private static IReadOnlyDictionary<string, string> ToStringMap(
            IReadOnlyDictionary<string, JsonElement> settings)
        {
            var map = new Dictionary<string, string>();
            if (settings != null)
            {
                foreach (var entry in settings)
                {
                    map[entry.Key] = ScopedJsonStore.GetString(settings, entry.Key);
                }
            }
            return map;
        }

        // js providerCredentialParams — exact RPC wire payload.
        public static Dictionary<string, object> BuildParams(
            ProviderCredentialSnapshot snapshot, string originClientId)
        {
            return new Dictionary<string, object>
            {
                ["p_profile_id"] = snapshot.ProfileId,
                ["p_origin_client_id"] = originClientId,
                ["p_credentials"] = snapshot.Values.Select(entry => (object)new Dictionary<string, object>
                {
                    ["provider"] = entry.Provider,
                    // js credentialJson: { [field]: String(value || "").trim() }
                    ["credential_json"] = new Dictionary<string, string>
                    {
                        [entry.Field] = (entry.Value ?? "").Trim()
                    }
                }).ToList()
            };
        }

        // js mergeProviderCredentialRows — remote wins per provider; throws on invalid payload.
        public static ProviderCredentialSnapshot MergeRows(
            ProviderCredentialSnapshot snapshot, JsonElement rows)
        {
            var remoteByProvider = new Dictionary<string, JsonElement>(
                StringComparer.Ordinal);
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var provider = "";
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("provider", out var providerElement) &&
                        providerElement.ValueKind == JsonValueKind.String)
                    {
                        provider = providerElement.GetString().Trim().ToLowerInvariant();
                    }
                    remoteByProvider[provider] = row.Clone();
                }
            }

            return new ProviderCredentialSnapshot
            {
                ProfileId = snapshot.ProfileId,
                Values = snapshot.Values.Select(local =>
                {
                    JsonElement remote;
                    if (!remoteByProvider.TryGetValue(local.Provider, out remote))
                    {
                        return local;
                    }
                    var payload = ParseCredentialJson(remote);
                    JsonElement fieldElement;
                    if (payload == null ||
                        !payload.Value.TryGetProperty(local.Field, out fieldElement) ||
                        fieldElement.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException(
                            "Invalid credential payload for " + local.Provider);
                    }
                    return new ProviderCredentialEntry
                    {
                        Provider = local.Provider,
                        Field = local.Field,
                        Value = fieldElement.GetString().Trim()
                    };
                }).ToList()
            };
        }

        // js parseCredentialJson: string → parsed JSON; object → itself; else null.
        internal static JsonElement? ParseCredentialJson(JsonElement row)
        {
            JsonElement value = default;
            if (row.ValueKind == JsonValueKind.Object)
            {
                if (!row.TryGetProperty("credential_json", out value) &&
                    !row.TryGetProperty("credentialJson", out value))
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                try
                {
                    return JsonDocument.Parse(value.GetString()).RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                return value.Clone();
            }
            return null;
        }

        // js snapshotsEqual: ordered JSON comparison of the values arrays.
        public static bool SnapshotsEqual(
            ProviderCredentialSnapshot left, ProviderCredentialSnapshot right)
        {
            var leftValues = left != null && left.Values != null ? left.Values : new List<ProviderCredentialEntry>();
            var rightValues = right != null && right.Values != null ? right.Values : new List<ProviderCredentialEntry>();
            if (leftValues.Count != rightValues.Count)
            {
                return false;
            }
            for (var index = 0; index < leftValues.Count; index++)
            {
                if (leftValues[index].Provider != rightValues[index].Provider ||
                    leftValues[index].Field != rightValues[index].Field ||
                    leftValues[index].Value != rightValues[index].Value)
                {
                    return false;
                }
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Local state
        // ------------------------------------------------------------------

        private async Task<ProviderCredentialSnapshot> SnapshotFromLocalAsync(
            long profileId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var debrid = await ReadScopedAsync(DebridSettingsKey, profileId).ConfigureAwait(false);
            var mdbList = await ReadScopedAsync(MdbListSettingsKey, profileId).ConfigureAwait(false);
            var animeSkip = await ReadScopedAsync(AnimeSkipSettingsKey, profileId).ConfigureAwait(false);
            return BuildSnapshot(profileId, debrid, mdbList, animeSkip);
        }

        private async Task<Dictionary<string, JsonElement>> ReadScopedAsync(
            string key, long profileId)
        {
            return await ScopedJsonStore.Create(key, _store)
                .GetAsync(profileId.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Pending-push bookkeeping (js markPending/clearPending/isPending)
        // ------------------------------------------------------------------

        private async Task<Dictionary<string, long>> ReadPendingProfilesAsync()
        {
            var raw = await LocalStore.GetAsync<Dictionary<string, long>>(
                PendingKey, _store, null).ConfigureAwait(false);
            return raw ?? new Dictionary<string, long>();
        }

        private static string ScopeKey(string ownerId, long profileId)
        {
            return (ownerId ?? "").Trim() + ":" +
                   profileId.ToString(CultureInfo.InvariantCulture);
        }

        private async Task MarkPendingAsync(SyncScope scope)
        {
            var pending = await ReadPendingProfilesAsync().ConfigureAwait(false);
            pending[scope.Key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000;
            await LocalStore.SetAsync(PendingKey, pending, _store).ConfigureAwait(false);
        }

        private async Task ClearPendingAsync(SyncScope scope)
        {
            var pending = await ReadPendingProfilesAsync().ConfigureAwait(false);
            pending.Remove(scope.Key);
            // JS also clears the legacy numeric-only key for this profile.
            pending.Remove(scope.ProfileId.ToString(CultureInfo.InvariantCulture));
            await LocalStore.SetAsync(PendingKey, pending, _store).ConfigureAwait(false);
        }

        private async Task<bool> IsPendingAsync(SyncScope scope)
        {
            var pending = await ReadPendingProfilesAsync().ConfigureAwait(false);
            return pending.ContainsKey(scope.Key);
        }

        private sealed class SyncScope
        {
            public string OwnerId { get; set; }
            public long ProfileId { get; set; }
            public string Key { get; set; }
        }

        // js currentScope: authenticated + active profile match + effective owner id.
        private async Task<SyncScope> CurrentScopeAsync(long resolvedProfileId, CancellationToken ct)
        {
            if (!_auth.IsAuthenticated)
            {
                return null;
            }
            var activeRaw = (_profileIds.GetActiveProfileId() ?? "").Trim();
            if (activeRaw.Length == 0)
            {
                activeRaw = "1";
            }
            if (activeRaw != resolvedProfileId.ToString(CultureInfo.InvariantCulture))
            {
                return null;
            }
            var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
            return new SyncScope
            {
                OwnerId = ownerId,
                ProfileId = resolvedProfileId,
                Key = ScopeKey(ownerId, resolvedProfileId)
            };
        }

        // js requireCurrentScope.
        private static void RequireCurrentScope(SyncScope current, SyncScope expected)
        {
            if (current == null || current.OwnerId != expected.OwnerId)
            {
                throw new InvalidOperationException(
                    "Provider credential sync target changed");
            }
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        public Task<bool> PushCurrentToRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    var scope = await CurrentScopeAsync(Resolve(profileId), ct).ConfigureAwait(false);
                    if (scope == null)
                    {
                        return false;
                    }
                    var snapshot = await SnapshotFromLocalAsync(scope.ProfileId, ct).ConfigureAwait(false);
                    await PushSnapshotAsync(snapshot, ct).ConfigureAwait(false);
                    RequireCurrentScope(
                        await CurrentScopeAsync(scope.ProfileId, ct).ConfigureAwait(false), scope);
                    await ClearPendingAsync(scope).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Provider credential sync push failed", error);
                    return false;
                }
            });
        }

        public Task<bool> SyncFromRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    var scope = await CurrentScopeAsync(Resolve(profileId), ct).ConfigureAwait(false);
                    if (scope == null)
                    {
                        return false;
                    }
                    var local = await SnapshotFromLocalAsync(scope.ProfileId, ct).ConfigureAwait(false);
                    if (await IsPendingAsync(scope).ConfigureAwait(false))
                    {
                        await PushSnapshotAsync(local, ct).ConfigureAwait(false);
                        await ClearPendingAsync(scope).ConfigureAwait(false);
                    }
                    await SeedSnapshotAsync(local, ct).ConfigureAwait(false);
                    var rows = await PullRowsAsync(scope.ProfileId, ct).ConfigureAwait(false);
                    RequireCurrentScope(
                        await CurrentScopeAsync(scope.ProfileId, ct).ConfigureAwait(false), scope);
                    var merged = MergeRows(local, rows);
                    if (!SnapshotsEqual(local, merged))
                    {
                        await ApplySnapshotAsync(merged, ct).ConfigureAwait(false);
                    }
                    RequireCurrentScope(
                        await CurrentScopeAsync(scope.ProfileId, ct).ConfigureAwait(false), scope);
                    _lastForegroundPullAtMs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000;
                    return !SnapshotsEqual(local, merged);
                }
                catch (Exception error)
                {
                    Warn("Provider credential sync failed; keeping local credentials", error);
                    return false;
                }
            });
        }

        private async Task PushSnapshotAsync(ProviderCredentialSnapshot snapshot, CancellationToken ct)
        {
            await _supabase.RpcAsync(
                PushRpc,
                BuildParams(snapshot, await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false)),
                true, ct).ConfigureAwait(false);
        }

        private async Task SeedSnapshotAsync(ProviderCredentialSnapshot snapshot, CancellationToken ct)
        {
            await _supabase.RpcAsync(
                SeedRpc,
                BuildParams(snapshot, await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false)),
                true, ct).ConfigureAwait(false);
        }

        private async Task<JsonElement> PullRowsAsync(long profileId, CancellationToken ct)
        {
            return await _supabase.RpcAsync(
                PullRpc,
                new Dictionary<string, object>
                {
                    ["p_profile_id"] = profileId
                },
                true, ct).ConfigureAwait(false);
        }

        // js applySnapshot: writes pulled credentials back into the local stores silently.
        private async Task ApplySnapshotAsync(ProviderCredentialSnapshot snapshot, CancellationToken ct)
        {
            foreach (var entry in snapshot.Values)
            {
                if (entry.Provider.StartsWith("debrid:", StringComparison.Ordinal))
                {
                    var providerId = entry.Provider.Substring("debrid:".Length);
                    if (providerId.Length == 0)
                    {
                        continue;
                    }
                    await SetProviderApiKeyForProfileAsync(
                        snapshot.ProfileId, providerId, entry.Value).ConfigureAwait(false);
                }
                else if (entry.Provider == MdbListProvider)
                {
                    var store = ScopedJsonStore.Create(MdbListSettingsKey, _store);
                    var partial = new Dictionary<string, JsonElement>();
                    ScopedJsonStore.SetString(partial, "apiKey", (entry.Value ?? "").Trim());
                    await store.SetAsync(Key(snapshot.ProfileId), partial).ConfigureAwait(false);
                }
                else if (entry.Provider == AnimeSkipProvider)
                {
                    var store = ScopedJsonStore.Create(AnimeSkipSettingsKey, _store);
                    var partial = new Dictionary<string, JsonElement>();
                    ScopedJsonStore.SetString(partial, "clientId", (entry.Value ?? "").Trim());
                    await store.SetAsync(Key(snapshot.ProfileId), partial).ConfigureAwait(false);
                }
            }
        }

        // js DebridSettingsStore.setProviderApiKeyForProfile core semantics.
        private async Task SetProviderApiKeyForProfileAsync(
            long profileId, string providerId, string apiKey)
        {
            var field = ProviderApiKeyField(providerId);
            if (field == null)
            {
                return;
            }
            var key = Key(profileId);
            var store = ScopedJsonStore.Create(DebridSettingsKey, _store);
            var current = await store.GetAsync(key).ConfigureAwait(false);

            var next = new Dictionary<string, JsonElement>(current);
            ScopedJsonStore.SetString(next, field, (apiKey ?? "").Trim());

            var partial = new Dictionary<string, JsonElement>();
            ScopedJsonStore.SetString(partial, field, ScopedJsonStore.GetString(next, field));

            // hasAnyVisibleKey deliberately excludes realdebrid (JS parity).
            var hasAnyVisibleKey =
                NonEmpty(next, "torboxApiKey") || NonEmpty(next, "premiumizeApiKey");
            if (!NonEmpty(partial, field) && !hasAnyVisibleKey)
            {
                partial["enabled"] = JsonDocument.Parse("false").RootElement.Clone();
            }

            var preferred = PreferredResolverProviderId(next);
            ScopedJsonStore.SetString(partial, "preferredResolverProviderId", preferred);

            await store.SetAsync(key, partial).ConfigureAwait(false);
        }

        private static bool NonEmpty(IReadOnlyDictionary<string, JsonElement> obj, string key)
        {
            return ScopedJsonStore.GetString(obj, key).Trim().Length > 0;
        }

        // js providerApiKeyField: unknown providers are ignored on apply.
        private static string ProviderApiKeyField(string providerId)
        {
            var normalized = (providerId ?? "").Trim().ToLowerInvariant();
            if (normalized == "torbox")
            {
                return "torboxApiKey";
            }
            if (normalized == "premiumize")
            {
                return "premiumizeApiKey";
            }
            if (normalized == "realdebrid")
            {
                return "realDebridApiKey";
            }
            return null;
        }

        // js normalizePreferredResolverProviderId.
        private static string PreferredResolverProviderId(
            IReadOnlyDictionary<string, JsonElement> source)
        {
            var preferred = ScopedJsonStore.GetString(source, "preferredResolverProviderId")
                .Trim().ToLowerInvariant();
            var connected = new List<string>();
            if (NonEmpty(source, "torboxApiKey"))
            {
                connected.Add("torbox");
            }
            if (NonEmpty(source, "premiumizeApiKey"))
            {
                connected.Add("premiumize");
            }
            // realdebrid is intentionally excluded from resolver candidates (JS parity).
            return connected.Contains(preferred) ? preferred : connected.Count > 0 ? connected[0] : "";
        }

        private long Resolve(long? profileId)
        {
            return SyncProfileIds.Resolve(profileId, _profileIds);
        }

        private static string Key(long profileId)
        {
            return profileId.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // Debounced queue push + foreground pull scheduling
        // ------------------------------------------------------------------

        // js queuePush: fire-and-forget scope lookup, mark pending, debounce a push.
        public bool QueuePush(long? profileId = null)
        {
            if (!_auth.IsAuthenticated)
            {
                return false;
            }
            var resolved = Resolve(profileId);
#pragma warning disable CS4014
            QueuePushCoreAsync(resolved);
#pragma warning restore CS4014
            return true;
        }

        private async Task QueuePushCoreAsync(long resolvedProfileId)
        {
            SyncScope scope;
            try
            {
                scope = await CurrentScopeAsync(resolvedProfileId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Warn("Provider credential sync scope lookup failed", error);
                return;
            }
            if (scope == null)
            {
                return;
            }
            await MarkPendingAsync(scope).ConfigureAwait(false);
            lock (_timerGate)
            {
                Timer existing;
                if (_pushTimers.TryGetValue(scope.Key, out existing))
                {
                    existing.Dispose();
                }
                var timer = new Timer(_ =>
                {
                    lock (_timerGate)
                    {
                        _pushTimers.Remove(scope.Key);
                    }
#pragma warning disable CS4014
                    PushCurrentToRemoteAsync(resolvedProfileId);
#pragma warning restore CS4014
                }, null, PushDebounceMs, Timeout.Infinite);
                _pushTimers[scope.Key] = timer;
            }
        }

        public long LastForegroundPullAtMs
        {
            get { return Interlocked.Read(ref _lastForegroundPullAtMs); }
        }

        public bool IsForegroundPullInFlight
        {
            get { return Volatile.Read(ref _foregroundPullInFlight); }
        }

        // js requestForegroundPull.
        public bool RequestForegroundPull(bool force = false)
        {
            if (!_auth.IsAuthenticated)
            {
                return false;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000;
            lock (_timerGate)
            {
                if (!force && (_foregroundPullTimer != null || _foregroundPullInFlight))
                {
                    return false;
                }
                if (!force &&
                    now - LastForegroundPullAtMs < ForegroundMinIntervalMs)
                {
                    return false;
                }
                if (_foregroundPullTimer != null)
                {
                    _foregroundPullTimer.Dispose();
                    _foregroundPullTimer = null;
                }
                var delayMs = force ? 0 : ForegroundDelayMs;
                _foregroundPullTimer = new Timer(_ =>
                {
                    lock (_timerGate)
                    {
                        _foregroundPullTimer = null;
                        if (!_auth.IsAuthenticated)
                        {
                            return;
                        }
                        _foregroundPullInFlight = true;
                    }
#pragma warning disable CS4014
                    SyncFromRemoteAsync(null).ContinueWith(_ =>
                    {
                        Volatile.Write(ref _foregroundPullInFlight, false);
                    });
#pragma warning restore CS4014
                }, null, delayMs, Timeout.Infinite);
                return true;
            }
        }

        // js cancelForegroundPull.
        public void CancelForegroundPull()
        {
            lock (_timerGate)
            {
                if (_foregroundPullTimer != null)
                {
                    _foregroundPullTimer.Dispose();
                    _foregroundPullTimer = null;
                }
            }
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
