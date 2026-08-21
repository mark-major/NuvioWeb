using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NuvioTV.Core.Debrid
{
    /// <summary>
    /// Resolves debrid streams to direct playable URLs. Verbatim port of
    /// js/core/debrid/directDebridResolver.js: TorBox / Premiumize /
    /// Real-Debrid client-resolve flows, local torrent cache pre-check,
    /// 15-minute resolve cache (max 100 entries) with in-flight dedup, and the
    /// status vocabulary stale/not_cached/error/service_degraded/disabled/
    /// missing_api_key/success.
    /// P2P/EngineFS handling is a seam only — buildLocalResolve produces the
    /// torrent resolve descriptor consumed here.
    /// </summary>
    public sealed class DirectDebridResolver
    {
        private const string TorboxBaseUrl = "https://api.torbox.app/";
        private const string PremiumizeBaseUrl = "https://www.premiumize.me/";
        private const string RealDebridBaseUrl = "https://api.real-debrid.com/rest/1.0/";

        private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromMinutes(15);
        private const int ResolveCacheMaxEntries = 100;

        private readonly DebridHttp _http;
        private readonly IDebridSettingsProvider _settingsProvider;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, CacheEntry> _resolvedCache =
            new Dictionary<string, CacheEntry>();
        private readonly Dictionary<string, Task<DebridResolveResult>> _inFlightResolves =
            new Dictionary<string, Task<DebridResolveResult>>();

        /// <summary>Clock seam for TTL tests.</summary>
        public Func<DateTimeOffset> Clock { get; set; }

        /// <summary>Fire-and-forget cleanup tasks (RD delete); exposed for deterministic tests.</summary>
        public Task LastCleanupTask { get; private set; }

        private sealed class CacheEntry
        {
            public DebridResolveResult Result { get; set; }
            public DateTimeOffset CachedAt { get; set; }
        }

        private sealed class LocalCacheStatus
        {
            public string Status { get; set; }
            public bool? Cached { get; set; }
            public DebridResolveResult Degraded { get; set; }
        }

        public DirectDebridResolver(HttpClient httpClient, IDebridSettingsProvider settingsProvider)
        {
            _http = new DebridHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)));
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            Clock = () => DateTimeOffset.UtcNow;
        }

        // ------------------------------------------------------------------
        // Stream predicates (js isMagnetLink/getStreamUrl/torrentMagnetUri/…)
        // ------------------------------------------------------------------

        private static bool IsMagnetLink(string value)
        {
            return (value ?? "").Trim().ToLowerInvariant().StartsWith("magnet:", StringComparison.Ordinal);
        }

        private static string StreamUrlOf(DebridStream stream)
        {
            return FirstNonEmpty(
                NonMagnet(stream?.Url),
                NonMagnet(stream?.ExternalUrl));
        }

        private static string NonMagnet(string value)
        {
            if (!string.IsNullOrEmpty(value) && !IsMagnetLink(value))
            {
                return value;
            }
            return null;
        }

        private static string TorrentMagnetUri(DebridStream stream)
        {
            return FirstNonEmpty(
                AsMagnet(stream?.Url),
                AsMagnet(stream?.ExternalUrl));
        }

        private static string AsMagnet(string value)
        {
            return IsMagnetLink(value) ? value : null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }

        private static DebridClientResolve ClientResolveOf(DebridStream stream)
        {
            return stream?.ClientResolve;
        }

        private static bool IsDirectDebrid(DebridStream stream)
        {
            var resolve = ClientResolveOf(stream);
            return resolve != null &&
                string.Equals((resolve.Type ?? "").ToLowerInvariant(), "debrid", StringComparison.Ordinal) &&
                DebridProviders.IsSupported(resolve.Service) &&
                resolve.IsCached == true;
        }

        private static bool NeedsLocalDebridResolve(DebridStream stream)
        {
            return !IsDirectDebrid(stream) &&
                StreamUrlOf(stream) == null &&
                (!string.IsNullOrEmpty(stream.InfoHash) || TorrentMagnetUri(stream) != null);
        }

        // ------------------------------------------------------------------
        // Magnet construction (js stableFingerprint/trackerUrl/buildMagnetUri)
        // ------------------------------------------------------------------

        /// <summary>js stableFingerprint: signed 32-bit rolling hash, abs, base36.</summary>
        internal static string StableFingerprint(string value)
        {
            var text = value ?? "";
            var hash = 0;
            foreach (var ch in text)
            {
                unchecked
                {
                    hash = (hash << 5) - hash + ch;
                }
            }
            uint magnitude = hash == int.MinValue ? 2147483648u : (uint)Math.Abs(hash);
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (magnitude == 0)
            {
                return "0";
            }
            var builder = new StringBuilder();
            while (magnitude > 0)
            {
                builder.Insert(0, digits[(int)(magnitude % 36)]);
                magnitude /= 36;
            }
            return builder.ToString();
        }

        private static string TrackerUrl(string source)
        {
            var value = (source ?? "").Trim();
            if (value.Length == 0 || value.ToLowerInvariant().StartsWith("dht:", StringComparison.Ordinal))
            {
                return null;
            }
            if (value.Length >= 8 && value.Substring(0, 8).Equals("tracker:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(8).Trim();
            }
            return value.Length > 0 ? value : null;
        }

        private static string BuildMagnetUri(DebridClientResolve resolve)
        {
            resolve = resolve ?? new DebridClientResolve();
            var existing = (resolve.MagnetUri ?? "").Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
            var hash = (resolve.InfoHash ?? "").Trim();
            if (hash.Length == 0)
            {
                return null;
            }
            var displayName = FirstNonEmpty(resolve.Filename, resolve.TorrentName)?.Trim() ?? "";
            var trackers = new List<string>();
            var sources = resolve.Sources ?? new List<string>();
            foreach (var source in sources)
            {
                var tracker = TrackerUrl(source);
                if (tracker != null && !trackers.Contains(tracker))
                {
                    trackers.Add(tracker);
                }
            }
            var builder = new StringBuilder("magnet:?xt=urn:btih:");
            builder.Append(Uri.EscapeDataString(hash));
            if (displayName.Length > 0)
            {
                builder.Append("&dn=").Append(Uri.EscapeDataString(displayName));
            }
            foreach (var tracker in trackers)
            {
                builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
            }
            return builder.ToString();
        }

        private static DebridClientResolve BuildLocalResolve(
            DebridStream stream,
            int? season,
            int? episode,
            string providerId)
        {
            var magnet = TorrentMagnetUri(stream);
            if (magnet == null)
            {
                magnet = BuildMagnetUri(new DebridClientResolve
                {
                    InfoHash = stream.InfoHash,
                    Sources = stream.Sources
                });
            }
            if (magnet == null)
            {
                return null;
            }
            return new DebridClientResolve
            {
                Type = "torrent",
                InfoHash = stream.InfoHash,
                FileIdx = stream.FileIdx,
                MagnetUri = magnet,
                Sources = stream.Sources ?? new List<string>(),
                TorrentName = FirstNonEmpty(stream.Title, stream.Name),
                Filename = stream.FilenameHint,
                Title = FirstNonEmpty(stream.Title, stream.Name),
                Season = season,
                Episode = episode,
                Service = providerId,
                IsCached = stream.DebridCacheStatus != null &&
                    stream.DebridCacheStatus.State == Models.DebridCacheStatus.StateCached
        };
        }

        private DebridClientResolve GetResolve(DebridStream stream, int? season, int? episode, DebridSettingsSnapshot settings)
        {
            var directResolve = ClientResolveOf(stream);
            if (directResolve != null)
            {
                return directResolve;
            }
            if (!NeedsLocalDebridResolve(stream))
            {
                return null;
            }
            var credential = DebridProviders.PreferredResolverService(settings.ToDictionary());
            if (credential == null ||
                !DebridProviders.Supports(credential.Provider.Id, DebridCapabilities.LocalTorrentResolve))
            {
                return null;
            }
            return BuildLocalResolve(stream, season, episode, credential.Provider.Id);
        }

        private static string CacheKeyFor(
            DebridStream stream,
            int? season,
            int? episode,
            DebridSettingsSnapshot settings)
        {
            var resolve = GetResolveStatic(stream, season, episode, settings);
            if (resolve == null)
            {
                return null;
            }
            var provider = DebridProviders.ById(resolve.Service);
            var apiKey = DebridProviders.ApiKeyFor(settings.ToDictionary(), provider?.Id);
            if (provider == null || apiKey.Length == 0)
            {
                return null;
            }
            var identity = FirstNonEmpty(resolve.InfoHash, resolve.MagnetUri, resolve.TorrentName, resolve.Filename);
            if (identity == null)
            {
                return null;
            }
            var filename = FirstNonEmpty(resolve.Filename, stream?.FilenameHint) ?? "";
            var effectiveSeason = season ?? resolve.Season;
            var effectiveEpisode = episode ?? resolve.Episode;
            return string.Join("|",
                provider.Id,
                StableFingerprint(apiKey),
                identity.Trim().ToLowerInvariant(),
                resolve.FileIdx?.ToString(CultureInfo.InvariantCulture) ?? "",
                filename.Trim().ToLowerInvariant(),
                effectiveSeason?.ToString(CultureInfo.InvariantCulture) ?? "",
                effectiveEpisode?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        private static DebridClientResolve GetResolveStatic(
            DebridStream stream,
            int? season,
            int? episode,
            DebridSettingsSnapshot settings)
        {
            var directResolve = ClientResolveOf(stream);
            if (directResolve != null)
            {
                return directResolve;
            }
            if (!NeedsLocalDebridResolve(stream))
            {
                return null;
            }
            var credential = DebridProviders.PreferredResolverService(settings.ToDictionary());
            if (credential == null ||
                !DebridProviders.Supports(credential.Provider.Id, DebridCapabilities.LocalTorrentResolve))
            {
                return null;
            }
            return BuildLocalResolve(stream, season, episode, credential.Provider.Id);
        }

        // ------------------------------------------------------------------
        // Public API (js canResolveStream/shouldListStream/cachedPlayableStream/resolve)
        // ------------------------------------------------------------------

        public bool CanResolveStream(DebridStream stream, int? season = null, int? episode = null)
        {
            var settings = _settingsProvider.Get();
            if (settings == null || !settings.Enabled)
            {
                return false;
            }
            var map = settings.ToDictionary();
            var resolve = GetResolve(stream, season, episode, settings);
            if (resolve == null)
            {
                return false;
            }
            var provider = DebridProviders.ById(resolve.Service);
            if (provider == null)
            {
                return false;
            }
            var activeProvider = DebridProviders.PreferredResolverService(map)?.Provider;
            if (IsDirectDebrid(stream) && activeProvider != null && provider.Id != activeProvider.Id)
            {
                return false;
            }
            if (NeedsLocalDebridResolve(stream) &&
                !provider.Capabilities.Contains(DebridCapabilities.LocalTorrentResolve))
            {
                return false;
            }
            if (NeedsLocalDebridResolve(stream) &&
                stream.DebridCacheStatus?.State == Models.DebridCacheStatus.StateNotCached)
            {
                return false;
            }
            return DebridProviders.ApiKeyFor(map, provider.Id).Length > 0;
        }

        public bool ShouldListStream(DebridStream stream, int? season = null, int? episode = null)
        {
            return StreamUrlOf(stream) != null ||
                !string.IsNullOrEmpty(stream?.YtId) ||
                CanResolveStream(stream, season, episode);
        }

        public DebridStream CachedPlayableStream(DebridStream stream, int? season = null, int? episode = null)
        {
            var key = CacheKeyFor(stream, season, episode, _settingsProvider.Get());
            var cached = key != null ? CachedResult(key) : null;
            return cached != null ? WithResolvedUrl(stream, cached) : null;
        }

        public async Task<DebridResolveOutcome> ResolveAsync(
            DebridStream stream,
            int? season = null,
            int? episode = null)
        {
            try
            {
                if (StreamUrlOf(stream) != null)
                {
                    return new DebridResolveOutcome
                    {
                        Status = DebridResolveStatus.Success,
                        Stream = stream
                    };
                }
                var settings = _settingsProvider.Get();
                if (settings == null || !settings.Enabled)
                {
                    return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.Disabled));
                }
                var map = settings.ToDictionary();
                var resolve = GetResolve(stream, season, episode, settings);
                if (resolve == null)
                {
                    return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.Stale));
                }
                var provider = DebridProviders.ById(resolve.Service);
                var apiKey = DebridProviders.ApiKeyFor(map, provider?.Id);
                if (provider == null || apiKey.Length == 0)
                {
                    return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.MissingApiKey));
                }
                var activeProvider = DebridProviders.PreferredResolverService(map)?.Provider;
                if (IsDirectDebrid(stream) && activeProvider != null && provider.Id != activeProvider.Id)
                {
                    return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.Stale));
                }
                if (NeedsLocalDebridResolve(stream) &&
                    stream.DebridCacheStatus?.State == Models.DebridCacheStatus.StateNotCached)
                {
                    return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.NotCached));
                }
                if (NeedsLocalDebridResolve(stream) &&
                    !string.IsNullOrEmpty(stream.InfoHash) &&
                    stream.DebridCacheStatus?.State != Models.DebridCacheStatus.StateCached &&
                    provider.Capabilities.Contains(DebridCapabilities.LocalTorrentCacheCheck))
                {
                    LocalCacheStatus cacheStatus;
                    try
                    {
                        cacheStatus = await GetLocalTorrentCacheStatus(provider, apiKey, stream.InfoHash)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.Error, error.Message));
                    }
                    if (cacheStatus.Status == DebridResolveStatus.ServiceDegraded)
                    {
                        return DebridResolveOutcome.FromResult(cacheStatus.Degraded);
                    }
                    if (cacheStatus.Status == "success" && cacheStatus.Cached == false)
                    {
                        return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.NotCached));
                    }
                }

                var key = CacheKeyFor(stream, season, episode, settings);
                if (key != null)
                {
                    var cached = CachedResult(key);
                    if (cached != null)
                    {
                        return SuccessOutcome(stream, cached);
                    }
                    Task<DebridResolveResult> inFlight;
                    lock (_cacheLock)
                    {
                        _inFlightResolves.TryGetValue(key, out inFlight);
                    }
                    if (inFlight != null)
                    {
                        var pending = await inFlight.ConfigureAwait(false);
                        return pending.Status == DebridResolveStatus.Success
                            ? SuccessOutcome(stream, pending)
                            : DebridResolveOutcome.FromResult(pending);
                    }
                }

                var task = RunProviderResolveAsync(provider.Id, resolve, apiKey, season, episode, stream);

                if (key != null)
                {
                    lock (_cacheLock)
                    {
                        _inFlightResolves[key] = task;
                    }
                }
                try
                {
                    var result = await task.ConfigureAwait(false);
                    if (key != null && result.Status == DebridResolveStatus.Success)
                    {
                        RememberResolved(key, result);
                    }
                    return result.Status == DebridResolveStatus.Success
                        ? SuccessOutcome(stream, result)
                        : DebridResolveOutcome.FromResult(result);
                }
                finally
                {
                    lock (_cacheLock)
                    {
                        if (_inFlightResolves.TryGetValue(key, out var current) && current == task)
                        {
                            _inFlightResolves.Remove(key);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                return DebridResolveOutcome.FromResult(DebridResolveResult.Failure(DebridResolveStatus.Error, error.Message));
            }
        }

        private async Task<DebridResolveResult> RunProviderResolveAsync(
            string providerId,
            DebridClientResolve resolve,
            string apiKey,
            int? season,
            int? episode,
            DebridStream stream)
        {
            switch (providerId)
            {
                case DebridProviderIds.Torbox:
                    return await ResolveTorboxAsync(resolve, apiKey, season, episode).ConfigureAwait(false);
                case DebridProviderIds.Premiumize:
                    return await ResolvePremiumizeAsync(resolve, apiKey, season, episode, stream)
                        .ConfigureAwait(false);
                case DebridProviderIds.RealDebrid:
                    return await ResolveRealDebridAsync(resolve, apiKey, season, episode).ConfigureAwait(false);
                default:
                    return DebridResolveResult.Failure(DebridResolveStatus.Error);
            }
        }

        private static DebridResolveOutcome SuccessOutcome(DebridStream stream, DebridResolveResult result)
        {
            return new DebridResolveOutcome
            {
                Status = DebridResolveStatus.Success,
                Stream = WithResolvedUrl(stream, result)
            };
        }

        // ------------------------------------------------------------------
        // Resolve cache (js cachedResult/rememberResolved)
        // ------------------------------------------------------------------

        private DebridResolveResult CachedResult(string cacheKey)
        {
            lock (_cacheLock)
            {
                if (!_resolvedCache.TryGetValue(cacheKey, out var entry))
                {
                    return null;
                }
                if (Clock() - entry.CachedAt > ResolveCacheTtl)
                {
                    _resolvedCache.Remove(cacheKey);
                    return null;
                }
                return entry.Result;
            }
        }

        private void RememberResolved(string cacheKey, DebridResolveResult result)
        {
            lock (_cacheLock)
            {
                _resolvedCache[cacheKey] = new CacheEntry { Result = result, CachedAt = Clock() };
                while (_resolvedCache.Count > ResolveCacheMaxEntries)
                {
                    string oldestKey = null;
                    DateTimeOffset oldest = DateTimeOffset.MaxValue;
                    foreach (var pair in _resolvedCache)
                    {
                        if (oldestKey == null || pair.Value.CachedAt < oldest)
                        {
                            oldestKey = pair.Key;
                            oldest = pair.Value.CachedAt;
                        }
                    }
                    if (oldestKey == null)
                    {
                        break;
                    }
                    _resolvedCache.Remove(oldestKey);
                }
            }
        }

        // ------------------------------------------------------------------
        // Provider flows
        // ------------------------------------------------------------------

        private static DebridResolveResult ServiceDegradedFailure(DebridHttpResponse response, string providerName)
        {
            if (response.Status != 502 && response.Status != 503 && response.Status != 504)
            {
                return null;
            }
            return DebridResolveResult.Failure(
                DebridResolveStatus.ServiceDegraded,
                providerName + " service returned HTTP " + response.Status + ".");
        }

        private async Task<DebridResolveResult> ResolveTorboxAsync(
            DebridClientResolve resolve,
            string apiKey,
            int? season,
            int? episode)
        {
            var magnet = BuildMagnetUri(resolve);
            if (magnet == null)
            {
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            var create = await _http.PostMultipartAsync(
                JoinUrl(TorboxBaseUrl, "v1/api/torrents/createtorrent"),
                new Dictionary<string, string>
                {
                    { "magnet", magnet },
                    { "add_only_if_cached", "true" },
                    { "allow_zip", "false" }
                },
                apiKey).ConfigureAwait(false);
            var createData = create.Prop("data");
            var torrentId = DebridHttpResponse.JsonToString(
                createData.HasValue ? ChildOrNull(createData.Value, "torrent_id") : null)
                ?? DebridHttpResponse.JsonToString(
                    createData.HasValue ? ChildOrNull(createData.Value, "id") : null);
            if (!create.Ok || string.IsNullOrEmpty(torrentId))
            {
                var degraded = ServiceDegradedFailure(create, "Torbox");
                if (degraded != null)
                {
                    return degraded;
                }
                if (create.Status == 409)
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.NotCached);
                }
                return DebridResolveResult.Failure(
                    create.Status == 401 || create.Status == 403
                        ? DebridResolveStatus.Error
                        : DebridResolveStatus.Stale);
            }

            var torrent = await _http.GetAsync(
                JoinUrl(TorboxBaseUrl, "v1/api/torrents/mylist") +
                    "?id=" + Uri.EscapeDataString(torrentId) + "&bypass_cache=true",
                apiKey).ConfigureAwait(false);
            var filesElement = torrent.DataIsObject &&
                torrent.Prop("data").HasValue &&
                ChildOrNull(torrent.Prop("data").Value, "data").HasValue
                ? ChildOrNull(ChildOrNull(torrent.Prop("data").Value, "data").Value, "files")
                : (JsonElement?)null;
            if (!torrent.Ok || !filesElement.HasValue || filesElement.Value.ValueKind != JsonValueKind.Array)
            {
                var degraded = ServiceDegradedFailure(torrent, "Torbox");
                if (degraded != null)
                {
                    return degraded;
                }
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            var files = ParseFiles(filesElement.Value);
            var file = DebridFileSelection.SelectFile(files, resolve, season, episode, "torbox");
            if (file == null)
            {
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }

            var query = "?token=" + Uri.EscapeDataString(apiKey.Trim()) +
                "&torrent_id=" + Uri.EscapeDataString(torrentId) +
                "&zip_link=false&redirect=false&append_name=false";
            if (file.Id != null)
            {
                query += "&file_id=" + Uri.EscapeDataString(file.Id);
            }
            var link = await _http.GetAsync(
                JoinUrl(TorboxBaseUrl, "v1/api/torrents/requestdl") + query,
                apiKey).ConfigureAwait(false);
            // js: json.data is the direct URL string (TorBox requestdl).
            var dataProp = link.Prop("data");
            var url = dataProp.HasValue && dataProp.Value.ValueKind == JsonValueKind.String
                ? dataProp.Value.GetString()
                : "";
            if (!link.Ok || url.Length == 0)
            {
                var degraded = ServiceDegradedFailure(link, "Torbox");
                if (degraded != null)
                {
                    return degraded;
                }
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            return DebridResolveResult.Success(url, DebridFileSelection.GetDisplayName(file), DebridFileSelection.GetFileSize(file));
        }

        private async Task<DebridResolveResult> ResolvePremiumizeAsync(
            DebridClientResolve resolve,
            string apiKey,
            int? season,
            int? episode,
            DebridStream stream)
        {
            var source = BuildMagnetUri(resolve) ?? StreamUrlOf(stream);
            if (source == null)
            {
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            var response = await _http.PostFormAsync(
                JoinUrl(PremiumizeBaseUrl, "api/transfer/directdl"),
                "src=" + Uri.EscapeDataString(source),
                apiKey).ConfigureAwait(false);
            if (!response.Ok)
            {
                return DebridResolveResult.Failure(
                    response.Status == 401 || response.Status == 403
                        ? DebridResolveStatus.Error
                        : DebridResolveStatus.Stale);
            }
            var statusText = DebridHttpResponse.JsonToString(response.Prop("status")) ?? "";
            if (statusText.ToLowerInvariant() == "error")
            {
                var message = ((DebridHttpResponse.JsonToString(response.Prop("message")) ?? "") + " " +
                    (DebridHttpResponse.JsonToString(response.Prop("code")) ?? "")).ToLowerInvariant();
                return DebridResolveResult.Failure(
                    message.Contains("cache") || message.Contains("not found")
                        ? DebridResolveStatus.NotCached
                        : DebridResolveStatus.Stale,
                    message);
            }
            var content = response.Prop("content");
            var files = content.HasValue && content.Value.ValueKind == JsonValueKind.Array
                ? ParseFiles(content.Value)
                : Array.Empty<DebridRemoteFile>();
            var file = DebridFileSelection.SelectFile(files, resolve, season, episode, "premiumize");
            var url = file?.Link ?? "";
            if (file == null || url.Length == 0)
            {
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            return DebridResolveResult.Success(
                url,
                FirstNonEmpty(DebridFileSelection.GetDisplayName(file), stream?.FilenameHint),
                FirstNonZero(DebridFileSelection.GetFileSize(file), stream?.VideoSizeHint));
        }

        private async Task<DebridResolveResult> ResolveRealDebridAsync(
            DebridClientResolve resolve,
            string apiKey,
            int? season,
            int? episode)
        {
            var magnet = BuildMagnetUri(resolve);
            if (magnet == null)
            {
                return DebridResolveResult.Failure(DebridResolveStatus.Stale);
            }
            var add = await _http.PostFormAsync(
                JoinUrl(RealDebridBaseUrl, "torrents/addMagnet"),
                "magnet=" + Uri.EscapeDataString(magnet),
                apiKey).ConfigureAwait(false);
            var torrentId = DebridHttpResponse.JsonToString(add.Prop("id"));
            if (!add.Ok || string.IsNullOrEmpty(torrentId))
            {
                return DebridResolveResult.Failure(
                    add.Status == 401 || add.Status == 403
                        ? DebridResolveStatus.Error
                        : DebridResolveStatus.Stale);
            }

            var resolved = false;
            try
            {
                var infoBefore = await TorrentInfoAsync(apiKey, torrentId).ConfigureAwait(false);
                var filesElement = infoBefore.Prop("files");
                if (!infoBefore.Ok || !filesElement.HasValue ||
                    filesElement.Value.ValueKind != JsonValueKind.Array)
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                var file = DebridFileSelection.SelectFile(
                    ParseFiles(filesElement.Value), resolve, season, episode, "realdebrid");
                if (file == null || file.Id == null)
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                var select = await _http.PostFormAsync(
                    JoinUrl(RealDebridBaseUrl, "torrents/selectFiles/") + Uri.EscapeDataString(torrentId),
                    "files=" + Uri.EscapeDataString(file.Id),
                    apiKey).ConfigureAwait(false);
                if (!select.Ok && select.Status != 202)
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                var infoAfter = await TorrentInfoAsync(apiKey, torrentId).ConfigureAwait(false);
                var afterStatus = DebridHttpResponse.JsonToString(infoAfter.Prop("status")) ?? "";
                if (!infoAfter.Ok || afterStatus.ToLowerInvariant() != "downloaded")
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                var links = infoAfter.Prop("links");
                string link = null;
                if (links.HasValue && links.Value.ValueKind == JsonValueKind.Array &&
                    links.Value.GetArrayLength() > 0)
                {
                    link = DebridHttpResponse.JsonToString(links.Value[0]);
                }
                if (string.IsNullOrEmpty(link))
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                var unrestricted = await _http.PostFormAsync(
                    JoinUrl(RealDebridBaseUrl, "unrestrict/link"),
                    "link=" + Uri.EscapeDataString(link),
                    apiKey).ConfigureAwait(false);
                var url = DebridHttpResponse.JsonToString(unrestricted.Prop("download")) ?? "";
                if (!unrestricted.Ok || url.Length == 0)
                {
                    return DebridResolveResult.Failure(DebridResolveStatus.Stale);
                }
                resolved = true;
                return DebridResolveResult.Success(
                    url,
                    FirstNonEmpty(
                        DebridHttpResponse.JsonToString(unrestricted.Prop("filename")),
                        DebridFileSelection.GetDisplayName(file)),
                    FirstNonNullableNumber(unrestricted.Prop("filesize"), DebridFileSelection.GetFileSize(file)));
            }
            finally
            {
                if (!resolved)
                {
                    var cleanup = Task.Run(async () =>
                    {
                        try
                        {
                            await _http.DeleteAsync(
                                JoinUrl(RealDebridBaseUrl, "torrents/delete/") +
                                    Uri.EscapeDataString(torrentId),
                                apiKey).ConfigureAwait(false);
                        }
                        catch
                        {
                            // js .catch(() => null)
                        }
                    });
                    LastCleanupTask = cleanup;
                }
            }
        }

        private Task<DebridHttpResponse> TorrentInfoAsync(string apiKey, string torrentId)
        {
            return _http.GetAsync(
                JoinUrl(RealDebridBaseUrl, "torrents/info/") + Uri.EscapeDataString(torrentId),
                apiKey);
        }

        private async Task<LocalCacheStatus> GetLocalTorrentCacheStatus(
            DebridProvider provider,
            string apiKey,
            string hash)
        {
            var normalized = (hash ?? "").Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return new LocalCacheStatus { Status = "unknown" };
            }
            if (provider.Id == DebridProviderIds.Torbox)
            {
                var response = await _http.PostJsonAsync(
                    JoinUrl(TorboxBaseUrl, "v1/api/torrents/checkcached?format=object"),
                    JsonSerializer.Serialize(new { hashes = new[] { normalized } }),
                    apiKey).ConfigureAwait(false);
                var degraded = ServiceDegradedFailure(response, "Torbox");
                if (degraded != null)
                {
                    return new LocalCacheStatus
                    {
                        Status = DebridResolveStatus.ServiceDegraded,
                        Degraded = degraded
                    };
                }
                if (!response.Ok || DebridHttpResponse.JsonToBool(response.Prop("success")) == false)
                {
                    return new LocalCacheStatus { Status = "unknown" };
                }
                var dataProp = response.Prop("data");
                var entry = dataProp.HasValue ? ChildOrNull(dataProp.Value, normalized) : null;
                return new LocalCacheStatus
                {
                    Status = "success",
                    Cached = entry.HasValue && IsTruthy(entry.Value)
                };
            }
            if (provider.Id == DebridProviderIds.Premiumize)
            {
                var body = "items%5B%5D=" + Uri.EscapeDataString("magnet:?xt=urn:btih:" + normalized);
                var response = await _http.PostFormAsync(
                    JoinUrl(PremiumizeBaseUrl, "api/cache/check"),
                    body,
                    apiKey).ConfigureAwait(false);
                var degraded = ServiceDegradedFailure(response, "Premiumize");
                if (degraded != null)
                {
                    return new LocalCacheStatus
                    {
                        Status = DebridResolveStatus.ServiceDegraded,
                        Degraded = degraded
                    };
                }
                var statusText = DebridHttpResponse.JsonToString(response.Prop("status")) ?? "";
                if (!response.Ok || statusText.ToLowerInvariant() == "error")
                {
                    return new LocalCacheStatus { Status = "unknown" };
                }
                var responseProp = response.Prop("response");
                bool cached = responseProp.HasValue &&
                    responseProp.Value.ValueKind == JsonValueKind.Array &&
                    responseProp.Value.GetArrayLength() > 0 &&
                    responseProp.Value[0].ValueKind == JsonValueKind.True;
                return new LocalCacheStatus { Status = "success", Cached = cached };
            }
            return new LocalCacheStatus { Status = "unknown" };
        }

        // ------------------------------------------------------------------
        // Stream rewrite (js withResolvedUrl)
        // ------------------------------------------------------------------

        private static DebridStream WithResolvedUrl(DebridStream stream, DebridResolveResult result)
        {
            var filename = FirstNonEmpty(result.Filename, stream.FilenameHint);
            long? videoSize = result.VideoSize.HasValue && result.VideoSize.Value != 0
                ? (long?)result.VideoSize.Value
                : stream.VideoSizeHint;
            return new DebridStream
            {
                Name = stream.Name,
                Title = stream.Title,
                Description = stream.Description,
                Url = result.Url,
                ExternalUrl = null,
                YtId = stream.YtId,
                InfoHash = stream.InfoHash,
                FileIdx = stream.FileIdx,
                Quality = stream.Quality,
                AddonName = stream.AddonName,
                AddonLogo = stream.AddonLogo,
                RawAddonName = stream.RawAddonName,
                FilenameHint = filename,
                VideoSizeHint = videoSize,
                DebridCacheStatus = stream.DebridCacheStatus,
                Sources = stream.Sources,
                ClientResolve = stream.ClientResolve
            };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string JoinUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private static JsonElement? ChildOrNull(JsonElement parent, string name)
        {
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value))
            {
                return value;
            }
            return null;
        }

        private static bool IsTruthy(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    return false;
                case JsonValueKind.Number:
                    return element.GetDouble() != 0;
                case JsonValueKind.String:
                    return element.GetString().Length > 0;
                default:
                    return true;
            }
        }

        private static double? FirstNonZero(double first, long? second)
        {
            if (first != 0)
            {
                return first;
            }
            return second.HasValue && second.Value != 0 ? (double?)second.Value : null;
        }

        private static double? FirstNonNullableNumber(JsonElement? element, double fallback)
        {
            var parsed = DebridHttpResponse.JsonToNumber(element);
            if (parsed.HasValue && parsed.Value != 0)
            {
                return parsed;
            }
            return fallback != 0 ? fallback : (double?)null;
        }

        /// <summary>Maps a provider JSON file array onto the normalized DTO.</summary>
        internal static IReadOnlyList<DebridRemoteFile> ParseFiles(JsonElement array)
        {
            var files = new List<DebridRemoteFile>();
            if (array.ValueKind != JsonValueKind.Array)
            {
                return files;
            }
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var file = new DebridRemoteFile
                {
                    Id = GetStringField(element, "id", "file_id"),
                    Name = GetStringField(element, "name"),
                    ShortName = GetStringField(element, "short_name", "shortName"),
                    Path = GetStringField(element, "path"),
                    AbsolutePath = GetStringField(element, "absolute_path", "absolutePath"),
                    MimeType = GetStringField(element, "mimetype", "mimeType", "mime_type"),
                    Size = GetNumberField(element, "size"),
                    Bytes = GetNumberField(element, "bytes"),
                    Link = GetStringField(element, "link")
                };
                files.Add(file);
            }
            return files;
        }

        private static string GetStringField(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var value))
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

        private static double? GetNumberField(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
            return null;
        }
    }
}
