using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Storage;
using NuvioTV.Core.Auth;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Portable watch-progress item (js watchProgressRepository item shape) plus
    /// the device-local display metadata Supabase never stores.
    /// </summary>
    public sealed class ProgressSyncItem
    {
        public string ContentId { get; set; }
        public string ContentType { get; set; } = "movie";
        public string VideoId { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public double? ProgressPercent { get; set; }
        public string Source { get; set; } = "local";
        public long UpdatedAt { get; set; }

        // Local-only metadata preserved across merges (preserveLocalProgressMetadata).
        public string Title { get; set; }
        public string Poster { get; set; }
        public string Background { get; set; }
        public string Logo { get; set; }
        public string EpisodeTitle { get; set; }
        public string ImdbId { get; set; }
        public string TmdbId { get; set; }
        public string TraktId { get; set; }
        public long? Year { get; set; }
        public string StreamIdentity { get; set; }
    }

    /// <summary>
    /// Watch-progress cloud sync. Verbatim port of js/core/profile/
    /// watchProgressSyncService.js: 3-way baseline merge, 60s min syncable
    /// duration, push signature dedupe with 120s failure backoff, single-flight
    /// push loop. Local repository access is injected via delegates (the native
    /// repository lands with Task 12.x); sync state persists in
    /// watchProgressSyncState.
    /// </summary>
    public sealed class WatchProgressSyncService
    {
        private const string PullRpc = "sync_pull_watch_progress";
        private const string PushRpc = "sync_push_watch_progress";
        private const string DeleteRpc = "sync_delete_watch_progress";
        internal const string SyntheticEpisodeVideoPrefix = "__nuvio_episode__:";
        internal const int PushRetryBackoffMs = 120000;
        internal const string SyncStateKey = "watchProgressSyncState";
        internal const long MinProgressSyncDurationMs = 60000;
        internal const long MaxAmbiguousSecondsProgressValue = 8 * 60 * 60;
        internal const long MaxReasonableProgressDurationMs = 24 * 60 * 60 * 1000;

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly ProfileManager _profiles;
        private readonly IKeyValueStore _store;
        private readonly ISyncClientIdProvider _clientIdProvider;
        private readonly Func<IReadOnlyList<ProgressSyncItem>> _getAllLocal;
        private readonly Action<IReadOnlyList<ProgressSyncItem>> _replaceAllLocal;
        private readonly Func<long> _utcNowMs;

        private readonly object _lock = new object();
        private Task<bool> _activePushPromise;
        private bool _pushAgainRequested;
        private string _lastSuccessfulPushSignature = "";
        private string _lastFailedPushSignature = "";
        private long _lastFailedPushAt;

        public WatchProgressSyncService(
            SupabaseClient supabase,
            AuthManager auth,
            ProfileManager profiles,
            IKeyValueStore store,
            ISyncClientIdProvider clientIdProvider,
            Func<IReadOnlyList<ProgressSyncItem>> getAllLocal,
            Action<IReadOnlyList<ProgressSyncItem>> replaceAllLocal,
            Func<long> utcNowMs = null)
        {
            _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clientIdProvider = clientIdProvider ??
                throw new ArgumentNullException(nameof(clientIdProvider));
            _getAllLocal = getAllLocal ?? (() => Array.Empty<ProgressSyncItem>());
            _replaceAllLocal = replaceAllLocal ?? (_ => { });
            _utcNowMs = utcNowMs ?? DefaultNow;
        }

        private static long DefaultNow()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // -------------------------------------------------------------------
        // Keys + normalization (watchProgressSyncService.js:27-66)
        // -------------------------------------------------------------------

        public static string ProgressKey(ProgressSyncItem item)
        {
            var contentId = (item?.ContentId ?? "").Trim();
            var videoId = string.IsNullOrEmpty(item?.VideoId) ? "main" : item.VideoId.Trim();
            var season = item?.Season == null ? "" : Convert.ToString(item.Season.Value, CultureInfo.InvariantCulture);
            var episode = item?.Episode == null ? "" : Convert.ToString(item.Episode.Value, CultureInfo.InvariantCulture);
            return contentId + "::" + videoId + "::" + season + "::" + episode;
        }

        public static IReadOnlyList<ProgressSyncItem> NormalizeProgressItems(
            IEnumerable<ProgressSyncItem> items)
        {
            var byKey = new Dictionary<string, ProgressSyncItem>();
            foreach (var item in items ?? Enumerable.Empty<ProgressSyncItem>())
            {
                if (item == null || string.IsNullOrEmpty(item.ContentId))
                {
                    continue;
                }
                var key = ProgressKey(item);
                byKey.TryGetValue(key, out var existing);
                if (existing == null || item.UpdatedAt > existing.UpdatedAt)
                {
                    byKey[key] = item;
                }
            }
            return byKey.Values
                .OrderByDescending(item => item.UpdatedAt)
                .ToList()
                .AsReadOnly();
        }

        public static string ProgressContentSignature(ProgressSyncItem item)
        {
            // JS parity (watchProgressSyncService.js:51-62): the portable-field
            // fingerprint includes videoId between contentType and season.
            return JsonSerializer.Serialize(new object[]
            {
                item?.ContentId ?? "",
                item?.ContentType ?? "movie",
                item?.VideoId ?? "",
                (long)(item?.Season ?? 0),
                (long)(item?.Episode ?? 0),
                item?.PositionMs ?? 0,
                item?.DurationMs ?? 0,
                item?.UpdatedAt ?? 0
            });
        }

        private static Dictionary<string, ProgressSyncItem> ItemsByProgressKey(
            IEnumerable<ProgressSyncItem> items)
        {
            var map = new Dictionary<string, ProgressSyncItem>();
            foreach (var item in NormalizeProgressItems(items))
            {
                map[ProgressKey(item)] = item;
            }
            return map;
        }

        /// <summary>
        /// js preserveLocalProgressMetadata: remote wins carry portable fields only —
        /// keep device-local display metadata + stream identity.
        /// </summary>
        private static ProgressSyncItem PreserveLocalProgressMetadata(
            ProgressSyncItem progress, ProgressSyncItem localItem)
        {
            if (progress == null || localItem == null)
            {
                return progress;
            }
            if (!string.IsNullOrEmpty(localItem.Title))
            {
                progress.Title = localItem.Title;
            }
            if (!string.IsNullOrEmpty(localItem.Poster))
            {
                progress.Poster = localItem.Poster;
            }
            if (!string.IsNullOrEmpty(localItem.Background))
            {
                progress.Background = localItem.Background;
            }
            if (!string.IsNullOrEmpty(localItem.Logo))
            {
                progress.Logo = localItem.Logo;
            }
            if (!string.IsNullOrEmpty(localItem.EpisodeTitle))
            {
                progress.EpisodeTitle = localItem.EpisodeTitle;
            }
            if (!string.IsNullOrEmpty(localItem.ImdbId))
            {
                progress.ImdbId = localItem.ImdbId;
            }
            if (!string.IsNullOrEmpty(localItem.TmdbId))
            {
                progress.TmdbId = localItem.TmdbId;
            }
            if (!string.IsNullOrEmpty(localItem.TraktId))
            {
                progress.TraktId = localItem.TraktId;
            }
            if (localItem.Year.HasValue)
            {
                progress.Year = localItem.Year;
            }
            if (!string.IsNullOrEmpty(localItem.StreamIdentity))
            {
                progress.StreamIdentity = localItem.StreamIdentity;
            }
            return progress;
        }

        // -------------------------------------------------------------------
        // Baseline snapshot state (js readBaselineItems/writeBaselineItems)
        // -------------------------------------------------------------------

        private async Task<IReadOnlyList<ProgressSyncItem>> ReadBaselineItemsAsync(string profileId)
        {
            var raw = await _store.GetAsync(SyncStateKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                return Array.Empty<ProgressSyncItem>();
            }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty(profileId ?? "", out var profileState) ||
                    profileState.ValueKind != JsonValueKind.Object ||
                    !profileState.TryGetProperty("remoteSnapshot", out var snapshot) ||
                    snapshot.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<ProgressSyncItem>();
                }
                var items = new List<ProgressSyncItem>();
                foreach (var entry in snapshot.EnumerateArray())
                {
                    items.Add(MapProgressRow(entry));
                }
                return NormalizeProgressItems(items);
            }
            catch (JsonException)
            {
                return Array.Empty<ProgressSyncItem>();
            }
        }

        private async Task WriteBaselineItemsAsync(string profileId, IEnumerable<ProgressSyncItem> items)
        {
            var state = new Dictionary<string, object>();
            var raw = await _store.GetAsync(SyncStateKey).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        state[property.Name] = JsonSerializer.Deserialize<JsonElement>(
                            property.Value.GetRawText());
                    }
                }
                catch (JsonException)
                {
                }
            }

            var normalized = NormalizeProgressItems(items).Select(item => new Dictionary<string, object>
            {
                ["contentId"] = item.ContentId,
                ["contentType"] = item.ContentType,
                ["videoId"] = item.VideoId,
                ["season"] = item.Season,
                ["episode"] = item.Episode,
                ["positionMs"] = item.PositionMs,
                ["durationMs"] = item.DurationMs,
                ["progressPercent"] = item.ProgressPercent,
                ["source"] = item.Source,
                ["updatedAt"] = item.UpdatedAt
            }).ToList();

            state[profileId ?? ""] = new Dictionary<string, object>
            {
                ["remoteSnapshot"] = normalized,
                ["updatedAt"] = _utcNowMs()
            };
            await _store.SetAsync(SyncStateKey, JsonSerializer.Serialize(state))
                .ConfigureAwait(false);
        }

        // -------------------------------------------------------------------
        // 3-way merge (js mergeProgressItems:113-169)
        // -------------------------------------------------------------------

        public static IReadOnlyList<ProgressSyncItem> MergeProgressItems(
            IEnumerable<ProgressSyncItem> localItems,
            IEnumerable<ProgressSyncItem> remoteItems,
            IEnumerable<ProgressSyncItem> baselineItems)
        {
            var localByKey = ItemsByProgressKey(localItems);
            var remoteByKey = ItemsByProgressKey(remoteItems);
            var baselineByKey = ItemsByProgressKey(baselineItems);

            var keys = new List<string>(localByKey.Keys);
            keys.AddRange(remoteByKey.Keys.Where(key => !localByKey.ContainsKey(key)));
            keys.AddRange(baselineByKey.Keys.Where(key =>
                !localByKey.ContainsKey(key) && !remoteByKey.ContainsKey(key)));

            var merged = new List<ProgressSyncItem>();
            foreach (var key in keys)
            {
                localByKey.TryGetValue(key, out var localItem);
                remoteByKey.TryGetValue(key, out var remoteItem);
                baselineByKey.TryGetValue(key, out var baselineItem);

                if (localItem != null && remoteItem != null)
                {
                    var localChanged = baselineItem == null ||
                        ProgressContentSignature(localItem) !=
                        ProgressContentSignature(baselineItem);
                    var remoteChanged = baselineItem == null ||
                        ProgressContentSignature(remoteItem) !=
                        ProgressContentSignature(baselineItem);
                    if (localChanged && !remoteChanged)
                    {
                        merged.Add(localItem);
                        continue;
                    }
                    if (remoteChanged && !localChanged)
                    {
                        merged.Add(PreserveLocalProgressMetadata(remoteItem, localItem));
                        continue;
                    }
                    var winner = localItem.UpdatedAt > remoteItem.UpdatedAt
                        ? localItem
                        : remoteItem;
                    merged.Add(PreserveLocalProgressMetadata(winner, localItem));
                    continue;
                }

                if (remoteItem != null)
                {
                    var remoteChanged = baselineItem != null &&
                        ProgressContentSignature(remoteItem) !=
                        ProgressContentSignature(baselineItem);
                    if (baselineItem == null || remoteChanged)
                    {
                        merged.Add(remoteItem);
                    }
                    continue;
                }

                if (localItem != null)
                {
                    var localChanged = baselineItem != null &&
                        ProgressContentSignature(localItem) !=
                        ProgressContentSignature(baselineItem);
                    if (baselineItem == null || localChanged)
                    {
                        merged.Add(localItem);
                    }
                }
            }

            return NormalizeProgressItems(merged);
        }

        // -------------------------------------------------------------------
        // Row mapping (js mapProgressRow:171-265)
        // -------------------------------------------------------------------

        public static ProgressSyncItem MapProgressRow(JsonElement row)
        {
            string GetStringProperty(params string[] names)
            {
                foreach (var name in names)
                {
                    if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value))
                    {
                        if (value.ValueKind == JsonValueKind.String)
                        {
                            return value.GetString();
                        }
                        if (value.ValueKind == JsonValueKind.Number)
                        {
                            return value.GetRawText();
                        }
                    }
                }
                return null;
            }

            JsonElement? GetProperty(params string[] names)
            {
                foreach (var name in names)
                {
                    if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value))
                    {
                        return value;
                    }
                }
                return null;
            }

            long? GetNullableLong(params string[] names)
            {
                var element = GetProperty(names);
                if (element.HasValue && element.Value.ValueKind == JsonValueKind.Number)
                {
                    return (long)element.Value.GetDouble();
                }
                return null;
            }

            var contentId = GetStringProperty("content_id", "contentId") ?? "";
            var contentType = GetStringProperty("content_type", "contentType") ?? "movie";
            var source = (GetStringProperty("source") ?? "").Trim();

            var updatedAtElement = GetProperty("updated_at", "last_watched", "lastWatched", "updatedAt");
            long updatedAt = _UpdatedAt(updatedAtElement, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            bool hasPositionMs = GetProperty("position_ms", "positionMs").HasValue;
            bool hasDurationMs = GetProperty("duration_ms", "durationMs").HasValue;
            double positionMsRaw = GetNumber(GetProperty("position_ms", "positionMs", "position"));
            double durationMsRaw = GetNumber(GetProperty("duration_ms", "durationMs", "duration"));
            var progressPercentRaw = GetProperty("progress_percent", "progressPercent");

            var seasonRaw = GetNullableLong("season", "season_number");
            var episodeRaw = GetNullableLong("episode", "episode_number");

            var rawVideoId = GetStringProperty("video_id", "videoId");
            var normalizedVideoId = rawVideoId != null && rawVideoId.Trim() == contentId
                ? null
                : rawVideoId;

            long ToMilliseconds(double value)
            {
                if (value <= 0)
                {
                    return 0;
                }
                return (long)Math.Truncate(value);
            }

            long NormalizeAmbiguousRemoteTime(double value)
            {
                if (value <= 0)
                {
                    return 0;
                }
                return (long)Math.Truncate(
                    value > MaxAmbiguousSecondsProgressValue ? value : value * 1000);
            }

            var positionMs = hasPositionMs
                ? ToMilliseconds(positionMsRaw)
                : NormalizeAmbiguousRemoteTime(positionMsRaw);
            var durationMs = hasDurationMs
                ? ToMilliseconds(durationMsRaw)
                : NormalizeAmbiguousRemoteTime(durationMsRaw);
            var normalizedTimes =
                NormalizeInflatedProgressTimes(positionMs, durationMs);

            double? normalizedProgressPercent = null;
            if (progressPercentRaw.HasValue &&
                progressPercentRaw.Value.ValueKind == JsonValueKind.Number)
            {
                var percent = progressPercentRaw.Value.GetDouble();
                if (!double.IsNaN(percent))
                {
                    normalizedProgressPercent = Math.Max(0, Math.Min(100, percent));
                }
            }
            double? completedProgressPercent = source == "trakt_history" &&
                normalizedProgressPercent.HasValue && normalizedProgressPercent.Value < 100
                ? 100
                : normalizedProgressPercent;

            return new ProgressSyncItem
            {
                ContentId = contentId,
                ContentType = contentType,
                VideoId = normalizedVideoId != null &&
                    normalizedVideoId.StartsWith(SyntheticEpisodeVideoPrefix, StringComparison.Ordinal)
                    ? null
                    : normalizedVideoId,
                Season = seasonRaw >= 0 ? seasonRaw : (long?)null,
                Episode = episodeRaw > 0 ? episodeRaw : (long?)null,
                PositionMs = normalizedTimes.Item1,
                DurationMs = normalizedTimes.Item2,
                ProgressPercent = completedProgressPercent,
                Source = source.Length > 0 ? source : "local",
                UpdatedAt = updatedAt
            };
        }

        private static long _UpdatedAt(JsonElement? element, Func<long> fallback)
        {
            if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Null ||
                element.Value.ValueKind == JsonValueKind.Undefined)
            {
                return fallback();
            }
            if (element.Value.ValueKind == JsonValueKind.Number)
            {
                var numeric = element.Value.GetDouble();
                if (!double.IsNaN(numeric) && !double.IsInfinity(numeric))
                {
                    return numeric > 1_000_000_000_000
                        ? (long)numeric
                        : (long)Math.Truncate(numeric * 1000);
                }
                return fallback();
            }
            if (element.Value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(element.Value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }
            return fallback();
        }

        private static double GetNumber(JsonElement? element)
        {
            if (element.HasValue && element.Value.ValueKind == JsonValueKind.Number)
            {
                return element.Value.GetDouble();
            }
            return 0;
        }

        public static Tuple<long, long> NormalizeInflatedProgressTimes(long positionMs, long durationMs)
        {
            if (durationMs > MaxReasonableProgressDurationMs &&
                durationMs / 1000 <= MaxReasonableProgressDurationMs)
            {
                return Tuple.Create(positionMs > 0 ? positionMs / 1000 : 0, durationMs / 1000);
            }
            return Tuple.Create(positionMs > 0 ? positionMs : 0,
                durationMs > 0 ? durationMs : 0);
        }

        // -------------------------------------------------------------------
        // Sync-item shaping (js 302-463)
        // -------------------------------------------------------------------

        internal static long? ToPositiveIntegerOrNull(long? value)
        {
            return value.HasValue && value.Value > 0 ? value.Value : (long?)null;
        }

        internal static long? ToNonNegativeIntegerOrNull(long? value)
        {
            return value.HasValue && value.Value >= 0 ? value.Value : (long?)null;
        }

        public static string ToRemoteVideoId(ProgressSyncItem item)
        {
            var explicitVideoId = (item?.VideoId ?? "").Trim();
            var contentId = (item?.ContentId ?? "").Trim();
            if (explicitVideoId.Length > 0 && explicitVideoId != "main" && explicitVideoId != contentId)
            {
                return explicitVideoId;
            }
            var season = ToNonNegativeIntegerOrNull(item?.Season);
            var episode = ToPositiveIntegerOrNull(item?.Episode);
            if (season != null || episode != null)
            {
                return SyntheticEpisodeVideoPrefix + (season ?? 0) + ":" + (episode ?? 0);
            }
            if (contentId.Length > 0)
            {
                return contentId;
            }
            return "main";
        }

        public static string ToProgressKey(ProgressSyncItem item)
        {
            var contentId = (item?.ContentId ?? "").Trim();
            var season = ToNonNegativeIntegerOrNull(item?.Season);
            var episode = ToPositiveIntegerOrNull(item?.Episode);
            if (contentId.Length > 0 && season != null && episode != null)
            {
                return contentId + "_s" + season + "e" + episode;
            }
            return contentId;
        }

        public static string SyncIdentityKey(ProgressSyncItem item)
        {
            var contentId = (item?.ContentId ?? "").Trim();
            var season = ToNonNegativeIntegerOrNull(item?.Season);
            var episode = ToPositiveIntegerOrNull(item?.Episode);
            if (contentId.Length > 0 && season != null && episode != null)
            {
                return contentId + ":episode:" + season + ":" + episode;
            }
            return contentId + ":video:" + ToRemoteVideoId(item);
        }

        public static IReadOnlyList<ProgressSyncItem> DedupeSyncItems(
            IEnumerable<ProgressSyncItem> items)
        {
            var byKey = new Dictionary<string, ProgressSyncItem>();
            foreach (var item in items ?? Enumerable.Empty<ProgressSyncItem>())
            {
                var contentId = (item?.ContentId ?? "").Trim();
                if (contentId.Length == 0)
                {
                    continue;
                }
                var key = ToProgressKey(item);
                byKey.TryGetValue(key, out var existing);
                if (existing == null || item.UpdatedAt > existing.UpdatedAt)
                {
                    byKey[key] = item;
                }
            }
            return byKey.Values.OrderByDescending(item => item.UpdatedAt).ToList().AsReadOnly();
        }

        public static IReadOnlyList<ProgressSyncItem> CoalesceSyncItems(
            IEnumerable<ProgressSyncItem> items)
        {
            var byIdentity = new Dictionary<string, ProgressSyncItem>();
            foreach (var item in DedupeSyncItems(items))
            {
                var key = SyncIdentityKey(item);
                byIdentity.TryGetValue(key, out var existing);
                if (existing == null || item.UpdatedAt > existing.UpdatedAt)
                {
                    byIdentity[key] = item;
                }
            }
            return byIdentity.Values.OrderByDescending(item => item.UpdatedAt).ToList().AsReadOnly();
        }

        public static bool IsSyncableProgressItem(ProgressSyncItem item)
        {
            return item?.DurationMs <= 0 ||
                item.DurationMs >= MinProgressSyncDurationMs;
        }

        internal static long RowFreshness(JsonElement row)
        {
            foreach (var name in new[] { "updated_at", "last_watched", "updatedAt" })
            {
                if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value))
                {
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        return (long)value.GetDouble();
                    }
                    if (value.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        return parsed.ToUnixTimeMilliseconds();
                    }
                }
            }
            return 0;
        }

        internal static IReadOnlyList<JsonElement> DedupeRowsForConflict(
            IEnumerable<JsonElement> rows, string onConflict)
        {
            var columns = (onConflict ?? "")
                .Split(',')
                .Select(column => column.Trim())
                .Where(column => column.Length > 0)
                .ToList();
            var materialized = rows?.ToList() ?? new List<JsonElement>();
            if (columns.Count == 0)
            {
                return materialized.AsReadOnly();
            }
            var byKey = new Dictionary<string, JsonElement>();
            foreach (var row in materialized)
            {
                var parts = columns.Select(column =>
                {
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty(column, out var value))
                    {
                        return value.ValueKind == JsonValueKind.Null ||
                            value.ValueKind == JsonValueKind.Undefined
                            ? ""
                            : value.ToString();
                    }
                    return "";
                });
                var key = string.Join("::", parts);
                byKey.TryGetValue(key, out var existing);
                if (existing.ValueKind == default || RowFreshness(row) >= RowFreshness(existing))
                {
                    byKey[key] = row;
                }
            }
            return byKey.Values.ToList().AsReadOnly();
        }

        internal static IReadOnlyList<JsonElement> DedupeRemoteProgressEntries(
            IEnumerable<JsonElement> rows)
        {
            return DedupeRowsForConflict(
                DedupeRowsForConflict(rows, "progress_key"),
                "content_id,video_id,season,episode");
        }

        public static IReadOnlyList<Dictionary<string, object>> BuildRemoteProgressEntries(
            IEnumerable<ProgressSyncItem> items)
        {
            var entries = (items ?? Enumerable.Empty<ProgressSyncItem>())
                .Select(item => new Dictionary<string, object>
                {
                    ["content_id"] = item.ContentId,
                    ["content_type"] = item.ContentType ?? "movie",
                    ["video_id"] = ToRemoteVideoId(item),
                    ["season"] = item.Season.HasValue
                        ? (object)item.Season.Value
                        : null,
                    ["episode"] = item.Episode.HasValue
                        ? (object)item.Episode.Value
                        : null,
                    ["position"] = Math.Max(0, item.PositionMs),
                    ["duration"] = Math.Max(0, item.DurationMs),
                    ["last_watched"] = item.UpdatedAt > 0 ? item.UpdatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["progress_key"] = ToProgressKey(item)
                })
                .ToList();
            // Typed dedupe (same conflict columns as DedupeRemoteProgressEntries)
            // without a JSON round-trip so values stay primitives for the RPC
            // payload and push signature.
            var byKey = DedupeEntryRowsBy(entries, new[] { "progress_key" });
            return DedupeEntryRowsBy(byKey,
                new[] { "content_id", "video_id", "season", "episode" }).AsReadOnly();
        }

        private static List<Dictionary<string, object>> DedupeEntryRowsBy(
            IEnumerable<Dictionary<string, object>> rows, string[] columns)
        {
            var byKey = new Dictionary<string, Dictionary<string, object>>();
            foreach (var row in rows)
            {
                var parts = columns.Select(column =>
                    row.TryGetValue(column, out var value) && value != null
                        ? value.ToString()
                        : "");
                var key = string.Join("::", parts);
                byKey.TryGetValue(key, out var existing);
                if (existing == null || EntryFreshness(row) >= EntryFreshness(existing))
                {
                    byKey[key] = row;
                }
            }
            return byKey.Values.ToList();
        }

        private static double EntryFreshness(IReadOnlyDictionary<string, object> row)
        {
            if (row.TryGetValue("last_watched", out var value) && value != null &&
                double.TryParse(value.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
            return 0;
        }

        public static IReadOnlyList<string> BuildDeleteKeys(IEnumerable<ProgressSyncItem> items)
        {
            var keys = new List<string>();
            foreach (var item in items ?? Enumerable.Empty<ProgressSyncItem>())
            {
                var key = ToProgressKey(item);
                if (key.Length > 0 && !keys.Contains(key))
                {
                    keys.Add(key);
                }
            }
            return keys.AsReadOnly();
        }

        public static string BuildPushSignature(
            IReadOnlyList<Dictionary<string, object>> rows)
        {
            var payload = (rows ?? Array.Empty<Dictionary<string, object>>()).Select(row => new object[]
            {
                row.TryGetValue("progress_key", out var pk) ? pk?.ToString() ?? "" : "",
                row.TryGetValue("video_id", out var vid) ? vid?.ToString() ?? "" : "",
                Convert.ToDouble(row.TryGetValue("season", out var s) ? s : 0, CultureInfo.InvariantCulture),
                Convert.ToDouble(row.TryGetValue("episode", out var e) ? e : 0, CultureInfo.InvariantCulture),
                Convert.ToDouble(row.TryGetValue("position", out var p) ? p : 0, CultureInfo.InvariantCulture),
                Convert.ToDouble(row.TryGetValue("duration", out var d) ? d : 0, CultureInfo.InvariantCulture),
                Convert.ToDouble(row.TryGetValue("last_watched", out var l) ? l : 0, CultureInfo.InvariantCulture)
            });
            return JsonSerializer.Serialize(payload);
        }

        // -------------------------------------------------------------------
        // Push / Pull / Delete (js pushOnce/pull/deleteItems)
        // -------------------------------------------------------------------

        private async Task<long> ResolveProfileIdAsync(CancellationToken ct)
        {
            var active = await _profiles.GetActiveProfileId().ConfigureAwait(false);
            return SyncProfileIds.Resolve(ParseOrNull(active), new FixedProfileProvider(active));

        }

        private static long? ParseOrNull(string value)
        {
            return long.TryParse((value ?? "").Trim(), out var parsed) ? parsed : (long?)null;
        }

        private sealed class FixedProfileProvider : IProfileIdProvider
        {
            private readonly string _value;

            public FixedProfileProvider(string value)
            {
                _value = value;
            }

            public string GetActiveProfileId()
            {
                return _value;
            }
        }

        private async Task<bool> PushOnceAsync(CancellationToken ct)
        {
            var pushSignature = "";
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }
                var items = CoalesceSyncItems(_getAllLocal())
                    .Where(IsSyncableProgressItem)
                    .ToList();
                var profileId = await ResolveProfileIdAsync(ct).ConfigureAwait(false);
                var rows = BuildRemoteProgressEntries(items);
                pushSignature = BuildPushSignature(rows);
                lock (_lock)
                {
                    if (pushSignature.Length > 0 && pushSignature == _lastSuccessfulPushSignature)
                    {
                        return true;
                    }
                    if (pushSignature.Length > 0 &&
                        pushSignature == _lastFailedPushSignature &&
                        _utcNowMs() - _lastFailedPushAt < PushRetryBackoffMs)
                    {
                        return false;
                    }
                }
                await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = profileId,
                    ["p_entries"] = rows,
                    ["p_origin_client_id"] = await _clientIdProvider
                        .GetClientIdAsync(ct).ConfigureAwait(false)
                }, true, ct).ConfigureAwait(false);
                lock (_lock)
                {
                    _lastSuccessfulPushSignature = pushSignature;
                    _lastFailedPushSignature = "";
                    _lastFailedPushAt = 0;
                }
                await WriteBaselineItemsAsync(
                    Convert.ToString(profileId, CultureInfo.InvariantCulture), items)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                lock (_lock)
                {
                    if (pushSignature.Length > 0)
                    {
                        _lastFailedPushSignature = pushSignature;
                    }
                    _lastFailedPushAt = _utcNowMs();
                }
                return false;
            }
        }

        public async Task<bool> PushAsync(CancellationToken ct = default)
        {
            Task<bool> active;
            bool joined;
            lock (_lock)
            {
                if (_activePushPromise != null)
                {
                    _pushAgainRequested = true;
                    active = _activePushPromise;
                    joined = true;
                }
                else
                {
                    active = RunPushLoopAsync(ct);
                    _activePushPromise = active;
                    joined = false;
                }
            }

            var result = await active.ConfigureAwait(false);
            if (joined)
            {
                return result;
            }
            try
            {
                return result;
            }
            finally
            {
                lock (_lock)
                {
                    if (_activePushPromise == active)
                    {
                        _activePushPromise = null;
                        _pushAgainRequested = false;
                    }
                }
            }
        }

        private async Task<bool> RunPushLoopAsync(CancellationToken ct)
        {
            var lastResult = false;
            while (true)
            {
                lock (_lock)
                {
                    _pushAgainRequested = false;
                }
                lastResult = await PushOnceAsync(ct).ConfigureAwait(false);
                bool again;
                lock (_lock)
                {
                    again = _pushAgainRequested;
                }
                if (!again)
                {
                    return lastResult;
                }
            }
        }

        public async Task<IReadOnlyList<ProgressSyncItem>> PullAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return Array.Empty<ProgressSyncItem>();
                }
                var localItems = _getAllLocal();
                var profileId = await ResolveProfileIdAsync(ct).ConfigureAwait(false);
                var profileIdText = Convert.ToString(profileId, CultureInfo.InvariantCulture);

                var rpcRows = await _supabase.RpcAsync(PullRpc,
                    new Dictionary<string, object> { ["p_profile_id"] = profileId },
                    true, ct).ConfigureAwait(false);
                var filteredRows = new List<ProgressSyncItem>();
                if (rpcRows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rpcRows.EnumerateArray())
                    {
                        var mapped = MapProgressRow(row);
                        if (mapped.ContentId.Length > 0 && IsSyncableProgressItem(mapped))
                        {
                            filteredRows.Add(mapped);
                        }
                    }
                }

                var snapshotItems = NormalizeProgressItems(filteredRows);
                var baselineItems = await ReadBaselineItemsAsync(profileIdText).ConfigureAwait(false);
                var mergedItems = MergeProgressItems(localItems, snapshotItems, baselineItems);
                await WriteBaselineItemsAsync(profileIdText, snapshotItems).ConfigureAwait(false);
                lock (_lock)
                {
                    _lastSuccessfulPushSignature = BuildPushSignature(
                        BuildRemoteProgressEntries(CoalesceSyncItems(snapshotItems)));
                }
                _replaceAllLocal(mergedItems);
                return mergedItems;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return Array.Empty<ProgressSyncItem>();
            }
        }

        public async Task<bool> DeleteItemsAsync(
            IEnumerable<ProgressSyncItem> items, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }
                var keys = BuildDeleteKeys(items);
                if (keys.Count == 0)
                {
                    return true;
                }
                var profileId = await ResolveProfileIdAsync(ct).ConfigureAwait(false);
                var profileIdText = Convert.ToString(profileId, CultureInfo.InvariantCulture);
                await _supabase.RpcAsync(DeleteRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = profileId,
                    ["p_keys"] = keys,
                    ["p_origin_client_id"] = await _clientIdProvider
                        .GetClientIdAsync(ct).ConfigureAwait(false)
                }, true, ct).ConfigureAwait(false);

                var baselineByKey = ItemsByProgressKey(
                    await ReadBaselineItemsAsync(profileIdText).ConfigureAwait(false));
                foreach (var item in NormalizeProgressItems(items))
                {
                    baselineByKey.Remove(ProgressKey(item));
                }
                await WriteBaselineItemsAsync(profileIdText, baselineByKey.Values)
                    .ConfigureAwait(false);
                lock (_lock)
                {
                    _lastSuccessfulPushSignature = "";
                }
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }
    }
}
