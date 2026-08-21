using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>Collections payload stored under the profile-scoped collectionsState key.</summary>
    public sealed class CollectionsPayload
    {
        [JsonPropertyName("collections")]
        public List<JsonElement> Collections { get; set; }
    }

    /// <summary>
    /// Collections sync. Behavioral spec (do not diverge):
    /// js/core/profile/collectionSyncService.js — sync_pull_collections /
    /// sync_push_collections with a stable-stringify change check and push debounce.
    ///
    /// Note: collection-entry shape normalization (js collectionsStore normalizeCollection)
    /// belongs to the CollectionsStore port; this service treats payloads as opaque JSON
    /// exactly like the wire format, so export/import round-trips preserve entries verbatim.
    /// </summary>
    public sealed class CollectionSyncService
    {
        private const string PullRpc = "sync_pull_collections";
        private const string PushRpc = "sync_push_collections";
        private const string StateKey = "collectionsState";
        public const int PushDebounceMs = 500;

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly IProfileIdProvider _profileIds;
        private readonly Action<string> _logWarning;

        private readonly HashSet<long> _syncingFromRemoteProfiles =
            new HashSet<long>();
        private readonly object _stateGate = new object();
        private readonly object _timerGate = new object();
        private readonly Dictionary<long, Timer> _pushTimers =
            new Dictionary<long, Timer>();

        public CollectionSyncService(
            SupabaseClient supabaseClient,
            AuthManager authManager,
            IKeyValueStore keyValueStore,
            IProfileIdProvider profileIdProvider = null,
            Action<string> logWarning = null)
        {
            _supabase = supabaseClient ?? throw new ArgumentNullException(nameof(supabaseClient));
            _auth = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _profileIds = profileIdProvider ?? new DefaultProfileIdProvider();
            _logWarning = logWarning;
        }

        public long Resolve(long? profileId)
        {
            return SyncProfileIds.Resolve(profileId, _profileIds);
        }

        // ------------------------------------------------------------------
        // Payload helpers
        // ------------------------------------------------------------------

        private ProfileScopedStore<CollectionsPayload> StateStore()
        {
            return ProfileScopedStore<CollectionsPayload>.CreateWithStore(
                StateKey,
                _store,
                normalize: value => new CollectionsPayload
                {
                    Collections =
                        value != null && value.Collections != null
                            ? value.Collections
                            : new List<JsonElement>()
                },
                merge: (current, partial) =>
                {
                    var next = partial != null ? partial : current;
                    return new CollectionsPayload
                    {
                        Collections = next != null && next.Collections != null
                            ? next.Collections
                            : new List<JsonElement>()
                    };
                });
        }

        // js CollectionsStore.importFromJson: {collections:[...]} or [...]; invalid → [].
        public static List<JsonElement> ImportCollectionsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<JsonElement>();
            }
            try
            {
                var root = JsonDocument.Parse(json).RootElement.Clone();
                return ExtractCollections(root);
            }
            catch (JsonException)
            {
                return new List<JsonElement>();
            }
        }

        private static List<JsonElement> ExtractCollections(JsonElement root)
        {
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("collections", out array) &&
                array.ValueKind == JsonValueKind.Array)
            {
                return array.EnumerateArray().Select(entry => entry.Clone()).ToList();
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root.EnumerateArray().Select(entry => entry.Clone()).ToList();
            }
            return new List<JsonElement>();
        }

        // js parseRemoteCollectionsPayload: blob.collections_json || blob.collectionsJson ||
        // blob, string or object.
        internal static List<JsonElement> ParseRemotePayload(JsonElement blob)
        {
            JsonElement raw = default;
            var found = false;
            if (blob.ValueKind == JsonValueKind.Object)
            {
                if (!blob.TryGetProperty("collections_json", out raw) &&
                    !blob.TryGetProperty("collectionsJson", out raw))
                {
                    raw = blob;
                }
                found = true;
            }
            else
            {
                raw = blob;
                found = blob.ValueKind == JsonValueKind.Array ||
                        blob.ValueKind == JsonValueKind.String;
            }

            if (!found)
            {
                return new List<JsonElement>();
            }
            if (raw.ValueKind == JsonValueKind.String)
            {
                return ImportCollectionsJson(raw.GetString());
            }
            try
            {
                return ImportCollectionsJson(
                    JsonSerializer.Serialize(raw));
            }
            catch (Exception)
            {
                return new List<JsonElement>();
            }
        }

        // js stableStringify: sorted-key deterministic JSON for change detection.
        public static string StableStringify(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    return "[" + string.Join(",", value.EnumerateArray().Select(StableStringify)) + "]";
                case JsonValueKind.Object:
                    var pairs = value.EnumerateObject()
                        .Select(property => new
                        {
                            Key = property.Name,
                            Value = StableStringify(property.Value.Clone())
                        })
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + pair.Value);
                    return "{" + string.Join(",", pairs) + "}";
                case JsonValueKind.String:
                    return JsonSerializer.Serialize(value.GetString());
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetRawText();
                default:
                    return "null";
            }
        }


        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        public bool IsSyncingFromRemote(long? profileId = null)
        {
            lock (_stateGate)
            {
                return _syncingFromRemoteProfiles.Contains(Resolve(profileId));
            }
        }

        public async Task<bool> PushAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            if (!_auth.IsAuthenticated)
            {
                return false;
            }
            var resolvedProfileId = Resolve(profileId);
            try
            {
                var payload = await StateStore()
                    .GetAsync(Key(resolvedProfileId)).ConfigureAwait(false);
                // JS round-trips through importFromJson before sending; the opaque-JSON port
                // keeps the same wire result for well-formed payloads.
                var parsedJson = ImportCollectionsJson(
                    JsonSerializer.Serialize(payload.Collections));
                await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = resolvedProfileId,
                    ["p_collections_json"] = parsedJson
                }, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception error)
            {
                Warn("Collection sync push failed", error);
                return false;
            }
        }

        public async Task<bool> PullAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            if (!_auth.IsAuthenticated)
            {
                return false;
            }
            var resolvedProfileId = Resolve(profileId);
            try
            {
                var rows = await _supabase.RpcAsync(PullRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = resolvedProfileId
                }, true, ct).ConfigureAwait(false);

                JsonElement? blob = null;
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        // js rows[0] || null: falsy (null) first rows fall through.
                        if (row.ValueKind == JsonValueKind.Null ||
                            row.ValueKind == JsonValueKind.Undefined)
                        {
                            continue;
                        }
                        blob = row.Clone();
                        break;
                    }
                }
                else if (rows.ValueKind == JsonValueKind.Object)
                {
                    blob = rows.Clone();
                }
                if (blob == null)
                {
                    return false;
                }

                var remoteCollections = ParseRemotePayload(blob.Value);
                var localPayload = await StateStore()
                    .GetAsync(Key(resolvedProfileId)).ConfigureAwait(false);
                if (StableStringify(Wrap(remoteCollections)) ==
                    StableStringify(Wrap(localPayload.Collections)))
                {
                    return false;
                }

                lock (_stateGate)
                {
                    _syncingFromRemoteProfiles.Add(resolvedProfileId);
                }
                try
                {
                    await StateStore().ReplaceAsync(
                        Key(resolvedProfileId),
                        new CollectionsPayload { Collections = remoteCollections })
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_stateGate)
                    {
                        _syncingFromRemoteProfiles.Remove(resolvedProfileId);
                    }
                }
                return true;
            }
            catch (Exception error)
            {
                Warn("Collection sync pull failed", error);
                return false;
            }
        }

        // js triggerPush: debounced per-profile push that skips while syncing from remote.
        public bool TriggerPush(long? profileId = null)
        {
            if (!_auth.IsAuthenticated)
            {
                return false;
            }
            var resolvedProfileId = Resolve(profileId);
            if (IsSyncingFromRemote(resolvedProfileId))
            {
                return false;
            }
            lock (_timerGate)
            {
                Timer existing;
                if (_pushTimers.TryGetValue(resolvedProfileId, out existing))
                {
                    existing.Dispose();
                }
                var timer = new Timer(_ =>
                {
                    lock (_timerGate)
                    {
                        _pushTimers.Remove(resolvedProfileId);
                    }
#pragma warning disable CS4014
                    PushAsync(resolvedProfileId);
#pragma warning restore CS4014
                }, null, PushDebounceMs, Timeout.Infinite);
                _pushTimers[resolvedProfileId] = timer;
                return true;
            }
        }

        // Test/integration surface: whether a debounce timer is pending for the profile.
        public bool IsPushQueued(long profileId)
        {
            lock (_timerGate)
            {
                return _pushTimers.ContainsKey(profileId);
            }
        }

        private static JsonElement Wrap(IReadOnlyList<JsonElement> collections)
        {
            return JsonSerializer.SerializeToElement(collections);
        }

        private static string Key(long profileId)
        {
            return profileId.ToString(CultureInfo.InvariantCulture);
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
