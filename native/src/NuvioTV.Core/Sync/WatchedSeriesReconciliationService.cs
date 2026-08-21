using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace NuvioTV.Core.Sync
{
    /// <summary>Watched-item store seam (js watchedItemsRepository).</summary>
    public interface IWatchedItemsStore
    {
        Task<IReadOnlyList<WatchedItemRecord>> GetAllAsync(int limit);
        Task<bool> IsWatchedAsync(string contentId);
        Task MarkAsync(WatchedMarkRequest item, bool skipTrackingWrite);
        Task UnmarkAsync(string contentId, bool rootOnly);
        Task UnmarkEpisodeAsync(
            string contentId, long? season, long? episode, string videoId, bool skipTrackingWrite);
    }

    public sealed class WatchedItemRecord
    {
        public string ContentId { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }
    }

    /// <summary>Watch-progress store seam (js watchProgressRepository).</summary>
    public interface IWatchProgressStore
    {
        Task<IReadOnlyList<WatchProgressRecord>> GetAllAsync();
        Task SaveProgressAsync(WatchProgressSaveRequest progress);
        Task RemoveProgressAsync(string contentId, string videoId);
    }

    public sealed class WatchProgressRecord
    {
        public string ContentId { get; set; }
        public string VideoId { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }

        /// <summary>js getWatchProgressFraction (domain/model/watchProgress.js).</summary>
        public double Fraction()
        {
            if (PositionMs > 0 && DurationMs > 0)
            {
                var fraction = (double)PositionMs / DurationMs;
                return Math.Max(0, Math.Min(1, fraction));
            }
            return 0;
        }
    }

    public sealed class WatchProgressSaveRequest
    {
        public string ContentId { get; set; }
        public string ContentType { get; set; }
        public string VideoId { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }
        public string Title { get; set; }
        public string EpisodeTitle { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public long UpdatedAtMs { get; set; }
    }

    /// <summary>Meta lookup seam (js metaRepository.getMetaFromAllAddons).</summary>
    public interface ISeriesMetaLookup
    {
        Task<SeriesMeta> GetMetaFromAllAddonsAsync(string contentType, string contentId);
    }

    /// <summary>Normalized series meta subset the reconciler consumes.</summary>
    public sealed class SeriesMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public IReadOnlyList<SeriesVideo> Videos { get; set; } = new List<SeriesVideo>();
    }

    public sealed class SeriesVideo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Name { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }

        public long? SeasonNumber { get; set; }
        public long? Number { get; set; }
        public long? EpisodeNumber { get; set; }
        public string Released { get; set; }
        public bool? Available { get; set; }
    }

    /// <summary>
    /// Series watched-marker reconciliation. Verbatim port of js/data/repository/
    /// watchedSeriesReconciliationService.js: when every released main episode is
    /// watched (via episode marks or completed progress), mark the series root —
    /// and unmark it again when episodes fall out of the watched set.
    /// </summary>
    public static class WatchedSeriesReconciliationService
    {
        private static readonly string[] SeriesTypes =
            { "series", "tv", "anime", "show", "tvshow" };

        public static bool IsSeriesType(string type)
        {
            return SeriesTypes.Contains((type ?? "").Trim().ToLowerInvariant());
        }

        internal static long? FirstPositiveInt(params long?[] values)
        {
            foreach (var value in values)
            {
                if (value.HasValue && value.Value > 0)
                {
                    return value.Value;
                }
            }
            return null;
        }

        public static bool TryParseSeasonEpisodeFromVideoId(
            string rawId, out long season, out long episode)
        {
            season = 0;
            episode = 0;
            var id = (rawId ?? "").Trim();
            if (id.Length == 0)
            {
                return false;
            }
            var parts = id.Split(':');
            if (parts.Length < 3)
            {
                return false;
            }
            if (!long.TryParse(parts[parts.Length - 2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out season) ||
                !long.TryParse(parts[parts.Length - 1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out episode))
            {
                return false;
            }
            if (season <= 0 || episode <= 0)
            {
                return false;
            }
            return true;
        }

        internal static NormalizedEpisode NormalizeEpisode(SeriesVideo video)
        {
            if (video == null || string.IsNullOrEmpty(video.Id))
            {
                return null;
            }

            long? parsedSeason = null;
            long? parsedEpisode = null;
            if (TryParseSeasonEpisodeFromVideoId(video.Id, out var pS, out var pE))
            {
                parsedSeason = pS;
                parsedEpisode = pE;
            }

            var episode = FirstPositiveInt(
                video.Episode, video.EpisodeNumber, video.Number, parsedEpisode);
            var season = FirstPositiveInt(video.Season, video.SeasonNumber, parsedSeason)
                ?? (episode.HasValue ? 1L : (long?)null);
            if (!episode.HasValue || !season.HasValue)
            {
                return null;
            }

            return new NormalizedEpisode
            {
                Id = video.Id,
                Title = !string.IsNullOrEmpty(video.Title)
                    ? video.Title
                    : (!string.IsNullOrEmpty(video.Name)
                        ? video.Name
                        : "S" + season + "E" + episode),
                Season = season.Value,
                Episode = episode.Value,
                Released = video.Released,
                Available = video.Available
            };
        }

        internal static bool IsReleasedEpisode(NormalizedEpisode episode, DateTimeOffset today)
        {
            if (episode.Available == false)
            {
                return false;
            }
            var rawDate = (episode.Released ?? "").Trim();
            if (rawDate.Length == 0)
            {
                return true;
            }
            return DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) && parsed <= today;
        }

        public static List<NormalizedEpisode> GetReleasedMainEpisodes(
            SeriesMeta meta, DateTimeOffset today)
        {
            var episodes = new List<NormalizedEpisode>();
            foreach (var video in meta?.Videos ?? (IReadOnlyList<SeriesVideo>)new List<SeriesVideo>())
            {
                var normalized = NormalizeEpisode(video);
                if (normalized != null && normalized.Season > 0 &&
                    IsReleasedEpisode(normalized, today))
                {
                    episodes.Add(normalized);
                }
            }
            episodes.Sort((left, right) =>
            {
                if (left.Season != right.Season)
                {
                    return left.Season.CompareTo(right.Season);
                }
                return left.Episode.CompareTo(right.Episode);
            });
            return episodes;
        }

        private static string WatchedEpisodeKey(long season, long episode)
        {
            return season + ":" + episode;
        }

        private static string ProgressEpisodeKey(WatchProgressRecord progress)
        {
            var season = FirstPositiveInt(progress.Season);
            var episode = FirstPositiveInt(progress.Episode);
            if (season.HasValue && episode.HasValue)
            {
                return WatchedEpisodeKey(season.Value, episode.Value);
            }
            return TryParseSeasonEpisodeFromVideoId(progress.VideoId, out var s, out var e)
                ? WatchedEpisodeKey(s, e)
                : "";
        }

        private static HashSet<string> BuildContentIds(string contentId, SeriesMeta meta)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in new[] { contentId, meta?.Id })
            {
                var clean = (value ?? "").Trim();
                if (clean.Length > 0)
                {
                    ids.Add(clean);
                }
            }
            return ids;
        }

        private static async Task<SeriesMeta> LoadSeriesMetaAsync(
            ISeriesMetaLookup lookup, string contentId, string contentType, SeriesMeta meta)
        {
            if (meta != null && meta.Videos != null)
            {
                return meta;
            }
            try
            {
                return await lookup.GetMetaFromAllAddonsAsync(
                    contentType ?? "series", contentId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // js console.warn swallow
                return null;
            }
        }

        private static async Task<bool> MarkReleasedEpisodesAsync(
            IWatchedItemsStore watchedItems,
            IWatchProgressStore progress,
            string contentId,
            string contentType,
            SeriesMeta meta,
            long watchedAtMs,
            bool skipTrackingWrite)
        {
            var episodes = GetReleasedMainEpisodes(
                meta, DateTimeOffset.FromUnixTimeMilliseconds(watchedAtMs));
            if (episodes.Count == 0)
            {
                return false;
            }
            foreach (var episode in episodes)
            {
                await watchedItems.MarkAsync(new WatchedMarkRequest
                {
                    ContentId = contentId,
                    ContentType = contentType,
                    Title = !string.IsNullOrEmpty(episode.Title)
                        ? episode.Title
                        : (!string.IsNullOrEmpty(meta?.Name) ? meta.Name : contentId),
                    Season = episode.Season,
                    Episode = episode.Episode,
                    WatchedAtMs = watchedAtMs
                }, skipTrackingWrite).ConfigureAwait(false);
                await progress.SaveProgressAsync(new WatchProgressSaveRequest
                {
                    ContentId = contentId,
                    ContentType = contentType,
                    VideoId = episode.Id,
                    Season = episode.Season,
                    Episode = episode.Episode,
                    Title = meta?.Name,
                    EpisodeTitle = string.IsNullOrEmpty(episode.Title) ? null : episode.Title,
                    PositionMs = 100,
                    DurationMs = 100,
                    UpdatedAtMs = watchedAtMs
                }).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>js reconcile(contentId, contentType, options).</summary>
        public static async Task<bool> ReconcileAsync(
            IWatchedItemsStore watchedItems,
            IWatchProgressStore progress,
            ISeriesMetaLookup metaLookup,
            string contentId,
            string contentType = "series",
            SeriesMeta meta = null,
            CompletedEpisode completedEpisode = null,
            string title = null,
            Action<string> enrichmentInvalidate = null)
        {
            var normalizedContentId = (contentId ?? "").Trim();
            var normalizedType = string.IsNullOrWhiteSpace(contentType) ? "series" : contentType.Trim();
            if (normalizedContentId.Length == 0 || !IsSeriesType(normalizedType))
            {
                return false;
            }

            meta = await LoadSeriesMetaAsync(metaLookup, normalizedContentId, normalizedType, meta)
                .ConfigureAwait(false);
            var episodes = GetReleasedMainEpisodes(meta ?? new SeriesMeta(), DateTimeOffset.UtcNow);
            if (episodes.Count == 0)
            {
                return false;
            }

            var contentIds = BuildContentIds(normalizedContentId, meta);

            IReadOnlyList<WatchedItemRecord> watchedItemList;
            try
            {
                watchedItemList = await watchedItems.GetAllAsync(5000).ConfigureAwait(false);
            }
            catch
            {
                watchedItemList = new List<WatchedItemRecord>();
            }
            IReadOnlyList<WatchProgressRecord> progressList;
            try
            {
                progressList = await progress.GetAllAsync().ConfigureAwait(false);
            }
            catch
            {
                progressList = new List<WatchProgressRecord>();
            }
            bool hasSeriesMarker;
            try
            {
                hasSeriesMarker = await watchedItems.IsWatchedAsync(normalizedContentId)
                    .ConfigureAwait(false);
            }
            catch
            {
                hasSeriesMarker = false;
            }

            var watchedEpisodeKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in watchedItemList)
            {
                if (item?.ContentId == null || !contentIds.Contains(item.ContentId.Trim()))
                {
                    continue;
                }
                if (item.Season.HasValue && item.Episode.HasValue)
                {
                    watchedEpisodeKeys.Add(WatchedEpisodeKey(item.Season.Value, item.Episode.Value));
                }
            }
            foreach (var record in progressList)
            {
                if (record == null || !contentIds.Contains((record.ContentId ?? "").Trim()) ||
                    record.Fraction() < Models.WatchProgress.CompletedThreshold)
                {
                    continue;
                }
                var key = ProgressEpisodeKey(record);
                if (key.Length > 0)
                {
                    watchedEpisodeKeys.Add(key);
                }
            }

            if (completedEpisode != null && completedEpisode.Season > 0 && completedEpisode.Episode > 0)
            {
                watchedEpisodeKeys.Add(
                    WatchedEpisodeKey(completedEpisode.Season.Value, completedEpisode.Episode.Value));
            }

            var allWatched = episodes.All(episode =>
                watchedEpisodeKeys.Contains(WatchedEpisodeKey(episode.Season, episode.Episode)));

            if (allWatched && !hasSeriesMarker)
            {
                await watchedItems.MarkAsync(new WatchedMarkRequest
                {
                    ContentId = normalizedContentId,
                    ContentType = normalizedType,
                    Title = !string.IsNullOrEmpty(meta?.Name)
                        ? meta.Name
                        : (!string.IsNullOrEmpty(title) ? title : normalizedContentId),
                    WatchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, true).ConfigureAwait(false);
                enrichmentInvalidate?.Invoke(normalizedContentId);
                return true;
            }

            if (!allWatched && hasSeriesMarker)
            {
                await watchedItems.UnmarkAsync(normalizedContentId, true).ConfigureAwait(false);
                enrichmentInvalidate?.Invoke(normalizedContentId);
                return true;
            }

            return false;
        }

        /// <summary>js markSeriesWatched.</summary>
        public static async Task<bool> MarkSeriesWatchedAsync(
            IWatchedItemsStore watchedItems,
            IWatchProgressStore progress,
            ISeriesMetaLookup metaLookup,
            string contentId,
            string contentType = "series",
            SeriesMeta meta = null,
            string title = null,
            Action<string> enrichmentInvalidate = null)
        {
            var normalizedContentId = (contentId ?? "").Trim();
            var normalizedType = string.IsNullOrWhiteSpace(contentType) ? "series" : contentType.Trim();
            if (normalizedContentId.Length == 0 || !IsSeriesType(normalizedType))
            {
                return false;
            }
            var watchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            meta = await LoadSeriesMetaAsync(metaLookup, normalizedContentId, normalizedType, meta)
                .ConfigureAwait(false);
            await watchedItems.MarkAsync(new WatchedMarkRequest
            {
                ContentId = normalizedContentId,
                ContentType = normalizedType,
                Title = !string.IsNullOrEmpty(meta?.Name)
                    ? meta.Name
                    : (!string.IsNullOrEmpty(title) ? title : normalizedContentId),
                WatchedAtMs = watchedAtMs
            }, false).ConfigureAwait(false);
            if (meta != null)
            {
                await MarkReleasedEpisodesAsync(
                    watchedItems, progress, normalizedContentId, normalizedType, meta,
                    watchedAtMs, true).ConfigureAwait(false);
            }
            enrichmentInvalidate?.Invoke(normalizedContentId);
            return true;
        }

        /// <summary>js unmarkSeriesWatched.</summary>
        public static async Task<bool> UnmarkSeriesWatchedAsync(
            IWatchedItemsStore watchedItems,
            IWatchProgressStore progress,
            string contentId,
            SeriesMeta meta = null,
            Action<string> enrichmentInvalidate = null)
        {
            var normalizedContentId = (contentId ?? "").Trim();
            if (normalizedContentId.Length == 0)
            {
                return false;
            }
            var episodes = GetReleasedMainEpisodes(meta ?? new SeriesMeta(), DateTimeOffset.UtcNow);
            await watchedItems.UnmarkAsync(normalizedContentId, false).ConfigureAwait(false);
            await progress.RemoveProgressAsync(normalizedContentId, null).ConfigureAwait(false);
            foreach (var episode in episodes)
            {
                await watchedItems.UnmarkEpisodeAsync(
                    normalizedContentId, episode.Season, episode.Episode, episode.Id, true)
                    .ConfigureAwait(false);
                await progress.RemoveProgressAsync(normalizedContentId, episode.Id)
                    .ConfigureAwait(false);
            }
            enrichmentInvalidate?.Invoke(normalizedContentId);
            return true;
        }
    }

    public sealed class NormalizedEpisode
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public long Season { get; set; }
        public long Episode { get; set; }
        public string Released { get; set; }
        public bool? Available { get; set; }
    }

    /// <summary>js options.completedEpisode.</summary>
    public sealed class CompletedEpisode
    {
        public long? Season { get; set; }
        public long? Episode { get; set; }
    }
}
