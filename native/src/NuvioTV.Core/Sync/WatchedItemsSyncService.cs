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
    /// One locally stored "watched" marker (JS watchedItemsStore.js item shape).
    /// </summary>
    public sealed class WatchedItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("profileId")]
        public string ProfileId { get; set; } = "1";

        [System.Text.Json.Serialization.JsonPropertyName("contentId")]
        public string ContentId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("contentType")]
        public string ContentType { get; set; } = "movie";

        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("season")]
        public double? Season { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("episode")]
        public double? Episode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("watchedAt")]
        public long WatchedAt { get; set; }
    }

    /// <summary>
    /// Remote sync of watched markers.
    ///
    /// Behavioral spec (do not diverge): js/core/profile/watchedItemsSyncService.js.
    /// Merge policy is REMOTE-WINS on key conflicts (ties included); local-only
    /// items survive a pull only when they were created after the last successful
    /// push. Pull pages through sync_pull_watched_items at 900 rows per page.
    /// </summary>
    public sealed class WatchedItemsSyncService
    {
        private const string PullRpc = "sync_pull_watched_items";
        private const string PushRpc = "sync_push_watched_items";
        private const string DeleteRpc = "sync_delete_watched_items";
        private const string SyncStateKey = "watchedItemsSyncState";
        private const string StoreKey = "watchedItems";
        internal const int WatchedItemsPageSize = 900;

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly ProfileManager _profiles;
        private readonly IKeyValueStore _store;
        private readonly Func<bool> _shouldUseSupabaseSync;
        private readonly Func<long> _utcNowMs;

        public WatchedItemsSyncService(
            SupabaseClient supabase,
            AuthManager auth,
            ProfileManager profiles,
            IKeyValueStore store,
            Func<bool> shouldUseSupabaseSync = null,
            Func<long> utcNowMs = null)
        {
            _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _shouldUseSupabaseSync = shouldUseSupabaseSync ?? DefaultGate;
            _utcNowMs = utcNowMs ?? DefaultNow;
        }

        private static bool DefaultGate()
        {
            return true;
        }

        private static long DefaultNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // -------------------------------------------------------------------
        // Keys + merge (watchedItemsSyncService.js:43-104)
        // -------------------------------------------------------------------

        // JS parity: watchedItemKey — "contentId:season:episode"; an empty
        // contentId yields a ":…"-prefixed key that the merge skips.
        public static string WatchedItemKey(WatchedItem item)
        {
            if (item == null)
            {
                return "::";
            }

            var contentId = (item.ContentId ?? "").Trim();
            var season = item.Season == null ? "" : item.Season.Value.ToString(CultureInfo.InvariantCulture);
            var episode = item.Episode == null ? "" : item.Episode.Value.ToString(CultureInfo.InvariantCulture);
            return contentId + ":" + season + ":" + episode;
        }

        // JS parity: mergeWatchedItems — remote-wins upsert, then local items
        // newer than the last successful push are re-added.
        public static IReadOnlyList<WatchedItem> MergeWatchedItems(
            IReadOnlyList<WatchedItem> localItems,
            IReadOnlyList<WatchedItem> remoteItems,
            long lastSuccessfulPushAt = 0)
        {
            var local = localItems ?? new WatchedItem[0];
            var remote = remoteItems ?? new WatchedItem[0];
            if (remote.Count == 0)
            {
                return local.ToList();
            }

            var byKey = new Dictionary<string, WatchedItem>();
            Action<WatchedItem, bool> upsert = (item, preferIncomingOnTie) =>
            {
                var key = WatchedItemKey(item);
                if (key.StartsWith(":", StringComparison.Ordinal))
                {
                    return;
                }

                WatchedItem existing;
                if (!byKey.TryGetValue(key, out existing) || existing == null)
                {
                    byKey[key] = item;
                    return;
                }

                var existingWatchedAt = existing.WatchedAt;
                var incomingWatchedAt = item.WatchedAt;
                if (incomingWatchedAt > existingWatchedAt ||
                    (incomingWatchedAt == existingWatchedAt && preferIncomingOnTie))
                {
                    byKey[key] = item;
                }
            };

            foreach (var item in remote)
            {
                upsert(item, true);
            }

            if (lastSuccessfulPushAt > 0)
            {
                foreach (var item in local)
                {
                    if (!byKey.ContainsKey(WatchedItemKey(item)) && item.WatchedAt > lastSuccessfulPushAt)
                    {
                        byKey[WatchedItemKey(item)] = item;
                    }
                }
            }

            return byKey.Values.OrderByDescending(item => item.WatchedAt).ToList();
        }

        // -------------------------------------------------------------------
        // Row mapping (watchedItemsSyncService.js:29-41, 106-128)
        // -------------------------------------------------------------------

        public static WatchedItem MapRemoteItem(JsonElement row)
        {
            var watchedAtRaw = SyncJson.GetProperty(row, "watched_at");
            if (watchedAtRaw.ValueKind == JsonValueKind.Undefined)
            {
                watchedAtRaw = SyncJson.GetProperty(row, "watchedAt");
            }

            var numeric = SyncJson.CoerceNumber(watchedAtRaw);
            long parsedDate;
            if (numeric.HasValue)
            {
                parsedDate = (long)Math.Truncate(numeric.Value);
            }
            else
            {
                var text = watchedAtRaw.ValueKind == JsonValueKind.String ? watchedAtRaw.GetString() : null;
                long epochMs;
                if (!string.IsNullOrEmpty(text) &&
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) &&
                    (epochMs = parsed.ToUnixTimeMilliseconds()) != 0)
                {
                    parsedDate = epochMs;
                }
                else
                {
                    parsedDate = 0;
                }
            }

            return new WatchedItem
            {
                ContentId = SyncJson.GetStringOrNull(row, "content_id", "contentId") ?? "",
                ContentType = SyncJson.GetStringOrNull(row, "content_type", "contentType") ?? "movie",
                Title = FirstNonEmpty(SyncJson.GetStringOrNull(row, "title", "name"), ""),
                Season = ToNumberOrNull(SyncJson.GetNumber(row, "season")),
                Episode = ToNumberOrNull(SyncJson.GetNumber(row, "episode")),
                WatchedAt = parsedDate != 0 ? parsedDate : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        // JS parity: toRemoteItem.
        public static Dictionary<string, object> ToRemoteItem(WatchedItem item)
        {
            return new Dictionary<string, object>
            {
                ["content_id"] = item.ContentId,
                ["content_type"] = string.IsNullOrEmpty(item.ContentType) ? "movie" : item.ContentType,
                ["title"] = item.Title ?? "",
                ["season"] = item.Season == null ? (object)null : item.Season.Value,
                ["episode"] = item.Episode == null ? (object)null : item.Episode.Value,
                ["watched_at"] = item.WatchedAt > 0 ? item.WatchedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        // JS parity: toDeleteKey.
        public static Dictionary<string, object> ToDeleteKey(WatchedItem item)
        {
            var key = new Dictionary<string, object>
            {
                ["content_id"] = item.ContentId
            };
            if (item.Season != null)
            {
                key["season"] = item.Season.Value;
            }

            if (item.Episode != null)
            {
                key["episode"] = item.Episode.Value;
            }

            return key;
        }

        // -------------------------------------------------------------------
        // Local persistence (watchedItemsStore.js semantics)
        // -------------------------------------------------------------------

        public async Task<IReadOnlyList<WatchedItem>> ListForProfileAsync(string profileId, int limit)
        {
            var all = await ReadAllAsync().ConfigureAwait(false);
            var pid = NormalizeProfileId(profileId);
            var filtered = all.Where(item => (item.ProfileId ?? "1") == pid).ToList();
            filtered.Sort((left, right) => right.WatchedAt.CompareTo(left.WatchedAt));
            return limit > 0 && filtered.Count > limit ? filtered.GetRange(0, limit) : filtered;
        }

        // JS parity: replaceForProfile — keeps other profiles' entries intact.
        public async Task ReplaceForProfileAsync(string profileId, IEnumerable<WatchedItem> items)
        {
            var pid = NormalizeProfileId(profileId);
            var normalized = (items ?? Enumerable.Empty<WatchedItem>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.ContentId))
                .Select(item => new WatchedItem
                {
                    ProfileId = string.IsNullOrEmpty(item.ProfileId) ? pid : item.ProfileId,
                    ContentId = item.ContentId ?? "",
                    ContentType = item.ContentType ?? "movie",
                    Title = item.Title ?? "",
                    Season = item.Season,
                    Episode = item.Episode,
                    WatchedAt = item.WatchedAt
                })
                .ToList();

            var all = await ReadAllAsync().ConfigureAwait(false);
            var next = normalized.Concat(all.Where(item => (item.ProfileId ?? "1") != pid)).ToList();
            next.Sort((left, right) => right.WatchedAt.CompareTo(left.WatchedAt));
            await LocalStore.SetAsync(StoreKey, next.Take(5000).ToList(), _store).ConfigureAwait(false);
        }

        private async Task<List<WatchedItem>> ReadAllAsync()
        {
            try
            {
                var json = await _store.GetAsync(StoreKey).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json))
                {
                    return new List<WatchedItem>();
                }

                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return new List<WatchedItem>();
                    }

                    return document.RootElement.EnumerateArray()
                        .Select(element => JsonSerializer.Deserialize<WatchedItem>(element.GetRawText(), LocalStore.JsonOptions))
                        .Where(item => item != null)
                        .ToList();
                }
            }
            catch (JsonException)
            {
                return new List<WatchedItem>();
            }
        }

        // JS parity: watchedStateForProfile / writeWatchedStateForProfile.
        private async Task<long> ReadLastSuccessfulPushAtAsync(string profileId)
        {
            var state = await LocalStore.GetAsync<Dictionary<string, WatchedSyncState>>(
                SyncStateKey, _store).ConfigureAwait(false);
            WatchedSyncState profileState;
            if (state != null && state.TryGetValue(NormalizeProfileId(profileId), out profileState) && profileState != null)
            {
                return profileState.LastSuccessfulPushAt;
            }

            return 0;
        }

        private Task WriteLastSuccessfulPushAtAsync(string profileId, long atMs)
        {
            var pid = NormalizeProfileId(profileId);
            return LocalStore.SetAsync(SyncStateKey, new Dictionary<string, WatchedSyncState>
            {
                [pid] = new WatchedSyncState { LastSuccessfulPushAt = atMs, UpdatedAt = _utcNowMs() }
            }, _store);
        }

        // -------------------------------------------------------------------
        // Service surface (watchedItemsSyncService.js:152-234)
        // -------------------------------------------------------------------

        public async Task<IReadOnlyList<WatchedItem>> PullAsync(string profileId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated || !_shouldUseSupabaseSync())
                {
                    return new List<WatchedItem>();
                }

                var resolvedProfileId = await ResolveProfileIdAsync().ConfigureAwait(false);
                var effectiveProfileId = profileId ?? resolvedProfileId;
                var localItems = await ListForProfileAsync(effectiveProfileId, 5000).ConfigureAwait(false);

                var rows = await PullRemoteRowsAsync(effectiveProfileId, ct).ConfigureAwait(false);
                var remoteItems = rows
                    .Select(MapRemoteItem)
                    .Where(item => !string.IsNullOrEmpty(item.ContentId))
                    .ToList();

                if (remoteItems.Count == 0 && localItems.Count > 0)
                {
                    return localItems;
                }

                var mergedItems = MergeWatchedItems(
                    localItems,
                    remoteItems,
                    await ReadLastSuccessfulPushAtAsync(effectiveProfileId).ConfigureAwait(false));
                await ReplaceForProfileAsync(effectiveProfileId, mergedItems).ConfigureAwait(false);
                return mergedItems;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return new List<WatchedItem>();
            }
        }

        // JS parity: pullRemoteWatchedItems — full pages loop until a short page.
        private async Task<List<JsonElement>> PullRemoteRowsAsync(string profileId, CancellationToken ct)
        {
            var allRows = new List<JsonElement>();
            var page = 1;
            while (true)
            {
                var result = await _supabase.RpcAsync(PullRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToLongOrNull(profileId),
                    ["p_page"] = page,
                    ["p_page_size"] = WatchedItemsPageSize
                }, true, ct).ConfigureAwait(false);

                var pageRows = result.ValueKind == JsonValueKind.Array
                    ? result.EnumerateArray().ToList()
                    : new List<JsonElement>();
                allRows.AddRange(pageRows);
                if (pageRows.Count < WatchedItemsPageSize)
                {
                    return allRows;
                }

                page++;
            }
        }

        public async Task<bool> PushAsync(string profileId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                var resolvedProfileId = await ResolveProfileIdAsync().ConfigureAwait(false);
                var effectiveProfileId = profileId ?? resolvedProfileId;
                var items = await ListForProfileAsync(effectiveProfileId, 5000).ConfigureAwait(false);
                await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToLongOrNull(effectiveProfileId),
                    ["p_items"] = items.Select(ToRemoteItem).ToArray()
                }, true, ct).ConfigureAwait(false);
                await WriteLastSuccessfulPushAtAsync(effectiveProfileId, _utcNowMs()).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        public async Task<bool> DeleteItemsAsync(IEnumerable<WatchedItem> items, string profileId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                if (!_shouldUseSupabaseSync())
                {
                    return true;
                }

                var keys = (items ?? Enumerable.Empty<WatchedItem>())
                    .Where(item => item != null && !string.IsNullOrEmpty(item.ContentId))
                    .Select(ToDeleteKey)
                    .ToArray();
                if (keys.Length == 0)
                {
                    return true;
                }

                var resolvedProfileId = await ResolveProfileIdAsync().ConfigureAwait(false);
                var effectiveProfileId = profileId ?? resolvedProfileId;
                await _supabase.RpcAsync(DeleteRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToLongOrNull(effectiveProfileId),
                    ["p_keys"] = keys
                }, true, ct).ConfigureAwait(false);
                await WriteLastSuccessfulPushAtAsync(effectiveProfileId, _utcNowMs()).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        // -------------------------------------------------------------------

        // JS parity: resolveProfileId — active profile id coerced to a positive
        // integer, defaulting to 1.
        public async Task<string> ResolveProfileIdAsync()
        {
            var raw = await _profiles.GetActiveProfileId().ConfigureAwait(false);
            return NormalizeProfileId(raw);
        }

        private static string NormalizeProfileId(string profileId)
        {
            var trimmed = (profileId ?? "").Trim();
            return trimmed.Length == 0 ? "1" : trimmed;
        }

        private static long ToLongOrNull(string value)
        {
            long parsed;
            return long.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                && parsed > 0
                ? parsed
                : 1;
        }

        private static double? ToNumberOrNull(double? value)
        {
            return value.HasValue ? value : null;
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return !string.IsNullOrEmpty(left) ? left : right;
        }
    }

    internal sealed class WatchedSyncState
    {
        [System.Text.Json.Serialization.JsonPropertyName("lastSuccessfulPushAt")]
        public long LastSuccessfulPushAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
        public long UpdatedAt { get; set; }
    }
}
