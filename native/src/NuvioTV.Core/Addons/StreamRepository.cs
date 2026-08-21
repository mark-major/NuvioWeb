using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Models;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Addons
{
    /// <summary>js streamOrigin sub-object (streamRepository.js:141-147, 214-218).</summary>
    public sealed class StreamGroupOrigin
    {
        [System.Text.Json.Serialization.JsonPropertyName("kind")]
        public string Kind { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonId")]
        public string AddonId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonBaseUrl")]
        public string AddonBaseUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonName")]
        public string AddonName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonOrderIndex")]
        public int? AddonOrderIndex { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sourceProviderId")]
        public string SourceProviderId { get; set; }
    }

    /// <summary>
    /// js per-addon stream group (streamRepository.js:135-164): identity fields,
    /// streamOrigin, and the resolved streams.
    /// </summary>
    public sealed class StreamGroup
    {
        [System.Text.Json.Serialization.JsonPropertyName("addonId")]
        public string AddonId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonBaseUrl")]
        public string AddonBaseUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonName")]
        public string AddonName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonLogo")]
        public string AddonLogo { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("addonOrderIndex")]
        public int AddonOrderIndex { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("streamOrigin")]
        public StreamGroupOrigin StreamOrigin { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("streams")]
        public IReadOnlyList<StreamItem> Streams { get; set; }
    }

    /// <summary>js safeApiCall envelope for a single addon's stream endpoint.</summary>
    public sealed class AddonStreamFetchResult
    {
        public bool Success { get; }
        public IReadOnlyList<StreamItem> Streams { get; }

        private AddonStreamFetchResult(bool success, IReadOnlyList<StreamItem> streams)
        {
            Success = success;
            Streams = streams ?? new List<StreamItem>();
        }

        public static AddonStreamFetchResult Ok(IReadOnlyList<StreamItem> streams)
        {
            return new AddonStreamFetchResult(true, streams);
        }

        public static AddonStreamFetchResult Fail()
        {
            return new AddonStreamFetchResult(false, null);
        }
    }

    /// <summary>
    /// Parallel stream resolution across installed addons. Port of
    /// js/data/repository/streamRepository.js: type resolution via
    /// resolveResourceRequestType, meta inline-stream fallback, group shape and
    /// ordering exactly as JS. Debrid presentation and plugin scrapers are not in
    /// this wave's scope (no native PluginManager/TmdbService/LocalDebrid yet), so
    /// the JS pluginTask/prepareDebridGroup steps are omitted.
    /// </summary>
    public sealed class StreamRepository
    {
        // JS resolves addons with Promise.all — unbounded parallelism. int.MaxValue
        // makes MapWithConcurrency spawn one worker per addon (same semantics).
        private const int AddonStreamConcurrency = int.MaxValue;

        private readonly StremioAddonClient _client;

        public StreamRepository(StremioAddonClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// js getStreamsFromAllAddons(type, videoId): fetch every capable addon in
        /// parallel, drop empty/failed addons, keep manifest order (addonOrderIndex).
        /// </summary>
        public async Task<IReadOnlyList<StreamGroup>> GetStreamsFromAllAddonsAsync(
            IReadOnlyList<Addon> installedAddons, string type, string videoId,
            CancellationToken ct = default)
        {
            var indexed = (installedAddons ?? Array.Empty<Addon>())
                .Select((addon, index) => new IndexedAddon(addon, index))
                .ToList();

            var results = await MapWithConcurrency.RunAsync(
                AddonStreamConcurrency,
                indexed,
                (entry, innerCt) => FetchAddonGroupAsync(entry.Addon, entry.Index, type, videoId, innerCt),
                ct).ConfigureAwait(false);

            return results
                .Where(group => group != null)
                .OrderBy(group => group.AddonOrderIndex)
                .ToList();
        }

        private async Task<StreamGroup> FetchAddonGroupAsync(
            Addon addon, int orderIndex, string type, string videoId, CancellationToken ct)
        {
            try
            {
                // Secondary/aggregator catalogs sometimes expose a channel row while
                // the original addon owns the ID under `tv`. Exact type remains the
                // default; an explicit, unambiguous idPrefix may recover the owner type.
                var streamRequestType = AddonRepository.ResolveResourceRequestType(
                    addon, "stream", type, videoId, allowIdTypeFallback: true);
                var metaRequestType = AddonRepository.ResolveResourceRequestType(
                    addon, "meta", type, videoId, allowIdTypeFallback: true);
                var canStream = !string.IsNullOrEmpty(streamRequestType);
                var canMeta = !string.IsNullOrEmpty(metaRequestType);
                // Meta-only stream discovery is a compatibility path for debrid cloud
                // items, which are exposed through the `other` type. Regular movie/series
                // metadata addons must not be queried as stream sources.
                var canTryMetaOnlyStreams =
                    canMeta &&
                    string.Equals((type ?? "").Trim().ToLowerInvariant(), "other", StringComparison.Ordinal);
                if (!canStream && !canTryMetaOnlyStreams)
                {
                    return null;
                }

                var addonStreams = new List<StreamItem>();
                var streamRequestSucceeded = false;
                if (canStream)
                {
                    var streamsResult = await GetStreamsFromAddonAsync(
                        addon.BaseUrl, streamRequestType, videoId, ct).ConfigureAwait(false);
                    if (streamsResult.Success)
                    {
                        streamRequestSucceeded = true;
                        if (streamsResult.Streams.Count > 0)
                        {
                            addonStreams = streamsResult.Streams.ToList();
                        }
                    }
                }

                // Match Android: when a declared stream endpoint succeeds with no
                // results, try the matching meta video's inline streams even if the
                // manifest omitted its meta resource. Keep the existing meta-only
                // compatibility path for debrid cloud `other` catalogs as well.
                if (addonStreams.Count == 0 &&
                    ((canStream && streamRequestSucceeded) || canTryMetaOnlyStreams))
                {
                    addonStreams = await FetchInlineStreamsFromMetaAsync(
                        addon,
                        canStream ? streamRequestType : metaRequestType,
                        videoId,
                        ct).ConfigureAwait(false);
                }

                if (addonStreams.Count == 0)
                {
                    return null;
                }

                foreach (var stream in addonStreams)
                {
                    stream.AddonName = addon.DisplayName;
                    stream.AddonLogo = addon.Logo;
                }

                return new StreamGroup
                {
                    AddonId = addon.Id,
                    AddonBaseUrl = addon.BaseUrl,
                    AddonName = addon.DisplayName,
                    AddonLogo = addon.Logo,
                    AddonOrderIndex = orderIndex,
                    StreamOrigin = new StreamGroupOrigin
                    {
                        Kind = "addon",
                        AddonId = addon.Id,
                        AddonBaseUrl = addon.BaseUrl,
                        AddonName = addon.DisplayName,
                        AddonOrderIndex = orderIndex
                    },
                    Streams = addonStreams
                };
            }
            catch (Exception)
            {
                // js catch (_) → null.
                return null;
            }
        }

        /// <summary>js getStreamsFromAddon(baseUrl, type, videoId).</summary>
        public async Task<AddonStreamFetchResult> GetStreamsFromAddonAsync(
            string baseUrl, string type, string videoId, CancellationToken ct = default)
        {
            JsonElement payload;
            try
            {
                payload = await _client.FetchStreamsAsync(baseUrl, type, videoId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return AddonStreamFetchResult.Fail();
            }

            var streams = StremioJson.Elements(StremioJson.ArrayOrEmpty(payload, "streams"))
                .Select(MapStream)
                .ToList();
            return AddonStreamFetchResult.Ok(streams);
        }

        /// <summary>
        /// js fetchInlineStreamsFromMeta(addon, type, videoId): try the content-level
        /// id (series episode ids like tt123:1:2) then the raw id (debrid cloud
        /// `other` items keyed dmm:&lt;torrentId&gt;); first non-empty inline stream list wins.
        /// </summary>
        public async Task<List<StreamItem>> FetchInlineStreamsFromMetaAsync(
            Addon addon, string type, string videoId, CancellationToken ct = default)
        {
            var rawVideoId = (videoId ?? "").Trim();
            if (string.IsNullOrEmpty(addon?.BaseUrl) || rawVideoId.Length == 0)
            {
                return new List<StreamItem>();
            }

            var contentLevelId = BuildContentLevelMetaId(rawVideoId);
            var candidateMetaIds = new List<string>();
            if (!string.IsNullOrEmpty(contentLevelId))
            {
                candidateMetaIds.Add(contentLevelId);
            }
            if (!string.IsNullOrEmpty(rawVideoId) && rawVideoId != contentLevelId)
            {
                candidateMetaIds.Add(rawVideoId);
            }

            foreach (var metaId in candidateMetaIds)
            {
                JsonElement payload;
                try
                {
                    payload = await _client.FetchMetaAsync(addon.BaseUrl, type, metaId, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    continue;
                }

                var meta = StremioJson.TryGet(payload, "meta", out var metaElement)
                    ? metaElement
                    : default;
                if (meta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var videos = StremioJson.Elements(StremioJson.ArrayOrEmpty(meta, "videos"));
                if (videos.Count == 0)
                {
                    continue;
                }

                JsonElement? matchingVideo = null;
                foreach (var video in videos)
                {
                    if (string.Equals(StremioJson.Str(video, "id"), rawVideoId, StringComparison.Ordinal))
                    {
                        matchingVideo = video;
                        break;
                    }
                }
                if (matchingVideo == null &&
                    !string.Equals(type, "series", StringComparison.Ordinal) &&
                    videos.Count == 1)
                {
                    matchingVideo = videos[0];
                }

                var streams = StremioJson.Elements(StremioJson.ArrayOrEmpty(matchingVideo ?? default, "streams"));

                var mapped = streams
                    .Select(MapStream)
                    .Where(stream =>
                        !string.IsNullOrEmpty(stream.Url) ||
                        !string.IsNullOrEmpty(stream.ExternalUrl) ||
                        !string.IsNullOrEmpty(stream.YtId) ||
                        stream.ClientResolve != null ||
                        !string.IsNullOrEmpty(stream.InfoHash))
                    .ToList();

                if (mapped.Count > 0)
                {
                    return mapped;
                }
            }

            return new List<StreamItem>();
        }

        // streamRepository.js:344-364 buildContentLevelMetaId.
        public static string BuildContentLevelMetaId(string videoId)
        {
            var raw = (videoId ?? "").Trim();
            if (raw.Length == 0)
            {
                return "";
            }
            var parts = raw.Split(':');
            if (parts.Length <= 1)
            {
                return raw;
            }
            var trailingNumericCount = 0;
            for (var index = parts.Length - 1; index >= 0; index--)
            {
                if (!IsNumeric(parts[index]))
                {
                    break;
                }
                trailingNumericCount++;
            }
            var firstSegment = parts[0];
            var minSegments =
                firstSegment.StartsWith("tt", StringComparison.OrdinalIgnoreCase) || IsNumeric(firstSegment)
                    ? 1
                    : 2;
            var segmentsToDrop = Math.Min(trailingNumericCount, Math.Max(0, parts.Length - minSegments));
            return segmentsToDrop > 0
                ? string.Join(":", parts.Take(parts.Length - segmentsToDrop))
                : raw;
        }

        private static bool IsNumeric(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }
            return true;
        }

        // streamRepository.js:256-284 mapStream.
        internal static StreamItem MapStream(JsonElement stream)
        {
            var result = new StreamItem
            {
                Name = OrNull(StremioJson.StrOrNull(stream, "name")),
                Title = OrNull(StremioJson.StrOrNull(stream, "title")),
                Description = OrNull(StremioJson.StrOrNull(stream, "description")),
                Url = OrNull(StremioJson.StrOrNull(stream, "url")),
                YtId = OrNull(StremioJson.StrOrNull(stream, "ytId")),
                InfoHash = OrNull(StremioJson.StrOrNull(stream, "infoHash")),
                FileIdx = StremioJson.NullableInt<int>(stream, "fileIdx"),
                ExternalUrl = OrNull(StremioJson.StrOrNull(stream, "externalUrl")),
                BehaviorHints = DeserializeOrNull<StreamBehaviorHints>(stream, "behaviorHints"),
                Quality = OrNull(StremioJson.StrOrNull(stream, "quality")),
                ClientResolve = DeserializeOrNull<ClientResolve>(stream, "clientResolve"),
                DebridCacheStatus = DeserializeOrNull<DebridCacheStatus>(stream, "debridCacheStatus")
            };

            var qualityValue = StremioJson.TryGet(stream, "qualityValue", out var qv) &&
                               qv.ValueKind == JsonValueKind.Number
                ? qv.GetDouble()
                : double.NaN;
            result.QualityValue = double.IsNaN(qualityValue) ? -1 : (int)qualityValue;

            var sources = new List<object>();
            foreach (var source in StremioJson.Elements(StremioJson.ArrayOrEmpty(stream, "sources")))
            {
                sources.Add(source.Clone());
            }
            result.Sources = sources;

            var sidecarSubtitles = new List<SubtitleItem>();
            foreach (var entry in StremioJson.Elements(StremioJson.ArrayOrEmpty(stream, "subtitles")))
            {
                var url = StremioJson.StrOrNull(entry, "url");
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }
                var id = StremioJson.StrOrNull(entry, "id");
                var lang = StremioJson.Str(entry, "lang");
                sidecarSubtitles.Add(new SubtitleItem(
                    string.IsNullOrEmpty(id) ? null : id,
                    url,
                    string.IsNullOrEmpty(lang) ? "unknown" : lang));
            }
            result.Subtitles = sidecarSubtitles;

            return result;
        }

        private static T DeserializeOrNull<T>(JsonElement element, string name)
            where T : class
        {
            if (StremioJson.TryGet(element, name, out var value) &&
                value.ValueKind == JsonValueKind.Object)
            {
                return StremioJson.Deserialize<T>(value);
            }
            return null;
        }

        private static string OrNull(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private readonly struct IndexedAddon
        {
            public readonly Addon Addon;
            public readonly int Index;

            public IndexedAddon(Addon addon, int index)
            {
                Addon = addon;
                Index = index;
            }
        }
    }
}
