using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NuvioTV.Core.Sync
{
    /// <summary>Playback context fanned out to tracking providers.</summary>
    public sealed class ScrobbleContext
    {
        public string ContentId { get; set; }
        public string ContentType { get; set; }
        public string Title { get; set; }
        public long? Year { get; set; }
        public string ImdbId { get; set; }
        public string TmdbId { get; set; }
        public string TraktId { get; set; }
        public string SimklId { get; set; }
        public string TvdbId { get; set; }
        public string MalId { get; set; }
        public string AnidbId { get; set; }
        public string AnilistId { get; set; }
        public string KitsuId { get; set; }
        public long? SeasonNumber { get; set; }
        public long? EpisodeNumber { get; set; }
        public string EpisodeTitle { get; set; }
        public string VideoId { get; set; }
        public double? ProgressPercent { get; set; }

        internal bool HasAnyExternalId()
        {
            return !string.IsNullOrEmpty(ImdbId) || !string.IsNullOrEmpty(TmdbId) ||
                !string.IsNullOrEmpty(TraktId);
        }
    }

    /// <summary>Watched-item write seam (js watchedItemsRepository.mark).</summary>
    public interface IWatchedMarker
    {
        Task MarkAsync(WatchedMarkRequest item);
    }

    public sealed class WatchedMarkRequest
    {
        public string ContentId { get; set; }
        public string ContentType { get; set; }
        public string Title { get; set; }
        public long? Season { get; set; }
        public long? Episode { get; set; }
        public long? WatchedAtMs { get; set; }
    }

    /// <summary>
    /// One tracking provider (js TraktScrobbleService / SimklScrobbleService):
    /// 15s start debounce, 3-strike failure cap, ≥80% stop → mark watched.
    /// </summary>
    public interface IScrobbleProvider
    {
        bool IsEnabled();
        void Start(ScrobbleContext context);
        void Pause(ScrobbleContext context);
        void Stop(ScrobbleContext context);
        void Cancel();
    }

    /// <summary>Shared scrobble constants (both JS services).</summary>
    public static class ScrobbleConstants
    {
        public const int StartDebounceMs = 15000;
        public const int MaxConsecutiveFailures = 3;
        public const double WatchedThresholdPercent = 80;

        internal static double NormalizeProgress(double? value)
        {
            var numeric = value ?? 0;
            if (numeric < 0)
            {
                numeric = 0;
            }
            if (numeric > 100)
            {
                numeric = 100;
            }
            return Math.Round(numeric * 100) / 100;
        }
    }

    /// <summary>
    /// Fan-out orchestrator. Verbatim port of js/data/repository/
    /// trackingScrobbleService.js: every action fans out to enabled providers.
    /// </summary>
    public sealed class TrackingScrobbleService
    {
        private readonly IReadOnlyList<IScrobbleProvider> _providers;

        public TrackingScrobbleService(params IScrobbleProvider[] providers)
        {
            _providers = providers ?? Array.Empty<IScrobbleProvider>();
        }

        private IEnumerable<IScrobbleProvider> EnabledProviders()
        {
            foreach (var provider in _providers)
            {
                if (provider.IsEnabled())
                {
                    yield return provider;
                }
            }
        }

        public bool IsEnabled()
        {
            foreach (var provider in EnabledProviders())
            {
                return true;
            }
            return false;
        }

        public void Start(ScrobbleContext context)
        {
            foreach (var provider in EnabledProviders())
            {
                provider.Start(context);
            }
        }

        public void Pause(ScrobbleContext context)
        {
            foreach (var provider in EnabledProviders())
            {
                provider.Pause(context);
            }
        }

        public void Stop(ScrobbleContext context)
        {
            foreach (var provider in EnabledProviders())
            {
                provider.Stop(context);
            }
        }

        public void Cancel()
        {
            foreach (var provider in _providers)
            {
                provider.Cancel();
            }
        }
    }

    /// <summary>
    /// Trakt provider. Verbatim port of js/data/repository/traktScrobbleService.js:
    /// payload builders, pause gating on lastAction, failure cap, threshold mark.
    /// Transport/auth are injected seams (wired to TraktClient at integration).
    /// </summary>
    public sealed class TraktScrobbleProvider : IScrobbleProvider
    {
        private readonly Func<CancellationToken, Task<string>> _accessTokenProvider;
        private readonly Func<string, object, string, CancellationToken, Task<bool>> _sender;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly IWatchedMarker _watchedMarker;
        private readonly Func<long> _utcNowMs;

        private CancellationTokenSource _startDebounceCts;
        private int _consecutiveFailures;
        private string _lastAction;
        private readonly object _lock = new object();

        public TraktScrobbleProvider(
            Func<CancellationToken, Task<string>> accessTokenProvider,
            Func<string, object, string, CancellationToken, Task<bool>> sender,
            IWatchedMarker watchedMarker,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<long> utcNowMs = null)
        {
            _accessTokenProvider = accessTokenProvider ??
                throw new ArgumentNullException(nameof(accessTokenProvider));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _watchedMarker = watchedMarker;
            _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public bool IsEnabled()
        {
            // JS: TraktAuthService.isAuthenticated(). The transport gate decides;
            // the send path re-checks and silently skips without a token.
            return true;
        }

        private static object BuildMoviePayload(ScrobbleContext context)
        {
            var movie = new Dictionary<string, object> { ["title"] = context.Title };
            if (context.Year.HasValue)
            {
                movie["year"] = context.Year.Value;
            }
            var ids = BuildIdsDictionary(
                new[] { "imdb", "tmdb", "trakt" },
                new[] { context.ImdbId, context.TmdbId, context.TraktId });
            if (ids.Count > 0)
            {
                movie["ids"] = ids;
            }
            return new Dictionary<string, object>
            {
                ["movie"] = movie,
                ["progress"] = context.ProgressPercent
            };
        }

        private static object BuildEpisodePayload(ScrobbleContext context)
        {
            var show = new Dictionary<string, object> { ["title"] = context.Title };
            if (context.Year.HasValue)
            {
                show["year"] = context.Year.Value;
            }
            var ids = BuildIdsDictionary(
                new[] { "imdb", "tmdb", "trakt" },
                new[] { context.ImdbId, context.TmdbId, context.TraktId });
            if (ids.Count > 0)
            {
                show["ids"] = ids;
            }
            var episode = new Dictionary<string, object>
            {
                ["season"] = context.SeasonNumber,
                ["number"] = context.EpisodeNumber
            };
            if (!string.IsNullOrEmpty(context.EpisodeTitle))
            {
                episode["title"] = context.EpisodeTitle;
            }
            return new Dictionary<string, object>
            {
                ["show"] = show,
                ["episode"] = episode,
                ["progress"] = context.ProgressPercent
            };
        }

        internal static Dictionary<string, object> BuildIdsDictionary(
            string[] keys, string[] values)
        {
            var ids = new Dictionary<string, object>();
            for (var index = 0; index < keys.Length; index++)
            {
                var value = values[index];
                if (!string.IsNullOrEmpty(value))
                {
                    ids[keys[index]] = value;
                }
            }
            return ids;
        }

        public static object BuildScrobblePayload(ScrobbleContext context)
        {
            return string.Equals(context.ContentType, "series", StringComparison.Ordinal)
                ? BuildEpisodePayload(context)
                : BuildMoviePayload(context);
        }

        private async Task MarkAsWatchedLocallyAsync(ScrobbleContext context)
        {
            if (_watchedMarker == null)
            {
                return;
            }
            try
            {
                await _watchedMarker.MarkAsync(new WatchedMarkRequest
                {
                    ContentId = context.ContentId,
                    WatchedAtMs = _utcNowMs(),
                    Season = context.ContentType == "series" ? context.SeasonNumber : null,
                    Episode = context.ContentType == "series" ? context.EpisodeNumber : null
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // js console.warn swallow
            }
        }

        private async Task SendScrobbleRequestAsync(string action, ScrobbleContext context)
        {
            lock (_lock)
            {
                if (_consecutiveFailures >= ScrobbleConstants.MaxConsecutiveFailures)
                {
                    return;
                }
            }

            if (!context.HasAnyExternalId())
            {
                return;
            }

            bool ok;
            try
            {
                var accessToken = await _accessTokenProvider(CancellationToken.None)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(accessToken))
                {
                    return;
                }

                ok = await _sender(action, BuildScrobblePayload(context), accessToken,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _consecutiveFailures++;
                }
                return;
            }

            lock (_lock)
            {
                if (ok)
                {
                    _consecutiveFailures = 0;
                    _lastAction = action;
                }
                else
                {
                    _consecutiveFailures++;
                }
            }

            if (ok && action == "stop" &&
                (context.ProgressPercent ?? 0) >= ScrobbleConstants.WatchedThresholdPercent)
            {
                await MarkAsWatchedLocallyAsync(context).ConfigureAwait(false);
            }
        }

        private void ClearStartTimer()
        {
            lock (_lock)
            {
                if (_startDebounceCts != null)
                {
                    try { _startDebounceCts.Cancel(); } catch (ObjectDisposedException) { }
                    _startDebounceCts.Dispose();
                    _startDebounceCts = null;
                }
            }
        }

        public void Start(ScrobbleContext context)
        {
            ClearStartTimer();
            CancellationTokenSource cts;
            lock (_lock)
            {
                _startDebounceCts = new CancellationTokenSource();
                cts = _startDebounceCts;
            }
            _ = RunDebouncedStartAsync(context, cts.Token);
        }

        private async Task RunDebouncedStartAsync(ScrobbleContext context, CancellationToken ct)
        {
            try
            {
                await _delay(TimeSpan.FromMilliseconds(ScrobbleConstants.StartDebounceMs), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Injectable delays may ignore the token; honor clearStartTimer() anyway.
            if (ct.IsCancellationRequested)
            {
                return;
            }
            await SendScrobbleRequestAsync("start", context).ConfigureAwait(false);
        }

        public void Pause(ScrobbleContext context)
        {
            ClearStartTimer();
            lock (_lock)
            {
                if (_lastAction == "start" || _lastAction == null)
                {
                }
                else
                {
                    return;
                }
            }
            _ = SendScrobbleRequestAsync("pause", context);
        }

        public void Stop(ScrobbleContext context)
        {
            ClearStartTimer();
            _ = SendStopAsync(context);
        }

        private async Task SendStopAsync(ScrobbleContext context)
        {
            await SendScrobbleRequestAsync("stop", context).ConfigureAwait(false);
            lock (_lock)
            {
                _lastAction = null;
            }
        }

        public void Cancel()
        {
            ClearStartTimer();
            lock (_lock)
            {
                _lastAction = null;
            }
        }
    }

    /// <summary>
    /// Simkl provider. Verbatim port of js/data/repository/simklScrobbleService.js:
    /// watermark normalizeProgress, anime videoId parsing, id stripping for anime
    /// seasons, pause gating on lastAction=="start". Snapshot-based id enrichment
    /// is an injected seam until SimklSyncService lands (6.2 follow-up).
    /// </summary>
    public sealed class SimklScrobbleProvider : IScrobbleProvider
    {
        private readonly Func<bool> _isAuthenticated;
        private readonly Func<string> _tokenProvider;
        private readonly Func<string, object, string, CancellationToken, Task<bool>> _sender;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly IWatchedMarker _watchedMarker;
        private readonly Func<long> _utcNowMs;

        private static readonly System.Text.RegularExpressions.Regex AnimeVideoMatch =
            new System.Text.RegularExpressions.Regex(
                @"^(mal|anidb|anilist|kitsu):(\d+):(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private CancellationTokenSource _startCts;
        private int _failures;
        private string _lastAction;
        private readonly object _lock = new object();

        public SimklScrobbleProvider(
            Func<bool> isAuthenticated,
            Func<string> tokenProvider,
            Func<string, object, string, CancellationToken, Task<bool>> sender,
            IWatchedMarker watchedMarker,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Func<long> utcNowMs = null)
        {
            _isAuthenticated = isAuthenticated ?? (() => false);
            _tokenProvider = tokenProvider ?? (() => null);
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _watchedMarker = watchedMarker;
            _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            _utcNowMs = utcNowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public bool IsEnabled()
        {
            return _isAuthenticated();
        }

        internal static bool TryParseAnimeVideoId(
            string videoId, out string idKey, out long idValue, out long episode)
        {
            idKey = null;
            idValue = 0;
            episode = 0;
            var match = AnimeVideoMatch.Match(videoId ?? "");
            if (!match.Success)
            {
                return false;
            }
            idKey = match.Groups[1].Value.ToLowerInvariant();
            idValue = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            episode = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            return true;
        }

        internal object BuildPayload(ScrobbleContext context)
        {
            var ids = BuildIds(context);

            var season = context.SeasonNumber;
            var episode = context.EpisodeNumber;
            var isAnime = TryParseAnimeVideoId(context.VideoId, out var idKey, out var idValue, out var animeEpisode);
            if (isAnime)
            {
                ids = new Dictionary<string, object> { [idKey] = idValue };
                season = null;
                episode = animeEpisode;
            }
            else if (season.HasValue && season.Value > 0 && LooksLikeAnime(context, ids))
            {
                foreach (var key in new[] { "simkl", "mal", "anidb", "anilist", "kitsu" })
                {
                    ids.Remove(key);
                }
            }

            var media = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(context.Title))
            {
                media["title"] = context.Title;
            }
            if (context.Year.HasValue)
            {
                media["year"] = context.Year.Value;
            }
            media["ids"] = ids;

            var payload = new Dictionary<string, object>
            {
                ["progress"] = ScrobbleConstants.NormalizeProgress(context.ProgressPercent)
            };
            if (context.ContentType != "series")
            {
                payload["movie"] = media;
            }
            else
            {
                payload[isAnime && season == null ? "anime" : "show"] = media;
                var episodePayload = new Dictionary<string, object>();
                if (season.HasValue)
                {
                    episodePayload["season"] = season.Value;
                }
                episodePayload["number"] = episode;
                if (!string.IsNullOrEmpty(context.EpisodeTitle))
                {
                    episodePayload["title"] = context.EpisodeTitle;
                }
                payload["episode"] = episodePayload;
            }
            return payload;
        }

        private static bool LooksLikeAnime(ScrobbleContext context, Dictionary<string, object> ids)
        {
            return ids.ContainsKey("mal") || ids.ContainsKey("anidb") ||
                ids.ContainsKey("anilist") || ids.ContainsKey("kitsu");
        }

        private static Dictionary<string, object> BuildIds(ScrobbleContext context)
        {
            return TraktScrobbleProvider.BuildIdsDictionary(
                new[] { "simkl", "imdb", "tmdb", "tvdb", "mal", "anidb", "anilist", "kitsu" },
                new[]
                {
                    context.SimklId, context.ImdbId, context.TmdbId, context.TvdbId,
                    context.MalId, context.AnidbId, context.AnilistId, context.KitsuId
                });
        }

        private async Task SendAsync(string action, ScrobbleContext context)
        {
            bool authenticated;
            lock (_lock)
            {
                authenticated = _isAuthenticated();
                if (!authenticated || _failures >= ScrobbleConstants.MaxConsecutiveFailures)
                {
                    return;
                }
            }

            if (context.ContentType == "series" && (context.EpisodeNumber ?? 0) == 0)
            {
                return;
            }

            bool ok;
            try
            {
                var token = _tokenProvider();
                if (string.IsNullOrEmpty(token))
                {
                    return;
                }

                ok = await _sender(action, BuildPayload(context), token,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _failures++;
                }
                return;
            }

            lock (_lock)
            {
                if (ok)
                {
                    _failures = 0;
                    _lastAction = action == "stop" ? null : action;
                }
                else
                {
                    _failures++;
                }
            }

            if (ok && action == "stop" &&
                (context.ProgressPercent ?? 0) >= ScrobbleConstants.WatchedThresholdPercent)
            {
                if (_watchedMarker != null)
                {
                    await _watchedMarker.MarkAsync(new WatchedMarkRequest
                    {
                        ContentId = context.ContentId,
                        ContentType = context.ContentType,
                        Title = context.Title,
                        Season = context.ContentType == "series" ? context.SeasonNumber : null,
                        Episode = context.ContentType == "series" ? context.EpisodeNumber : null,
                        WatchedAtMs = _utcNowMs()
                    }).ConfigureAwait(false);
                }
            }
        }

        private void ClearStartTimer()
        {
            lock (_lock)
            {
                if (_startCts != null)
                {
                    try { _startCts.Cancel(); } catch (ObjectDisposedException) { }
                    _startCts.Dispose();
                    _startCts = null;
                }
            }
        }

        public void Start(ScrobbleContext context)
        {
            ClearStartTimer();
            CancellationTokenSource cts;
            lock (_lock)
            {
                _startCts = new CancellationTokenSource();
                cts = _startCts;
            }
            _ = RunDebouncedStartAsync(context, cts.Token);
        }

        private async Task RunDebouncedStartAsync(ScrobbleContext context, CancellationToken ct)
        {
            try
            {
                await _delay(TimeSpan.FromMilliseconds(ScrobbleConstants.StartDebounceMs), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // Injectable delays may ignore the token; honor clearStartTimer() anyway.
            if (ct.IsCancellationRequested)
            {
                return;
            }
            await SendAsync("start", context).ConfigureAwait(false);
        }

        public void Pause(ScrobbleContext context)
        {
            ClearStartTimer();
            lock (_lock)
            {
                if (_lastAction != "start")
                {
                    return;
                }
            }
            _ = SendAsync("pause", context);
        }

        public void Stop(ScrobbleContext context)
        {
            ClearStartTimer();
            _ = SendAsync("stop", context);
        }

        public void Cancel()
        {
            ClearStartTimer();
            lock (_lock)
            {
                _failures = 0;
                _lastAction = null;
            }
        }
    }
}
