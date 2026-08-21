using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Models;

namespace NuvioTV.Core.Addons
{
    /// <summary>Optional lookup parameters (js options bag in subtitleRepository.js).</summary>
    public sealed class SubtitleRequestOptions
    {
        public string Title { get; set; }
        public string Year { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("videoHash")]
        public string VideoHash { get; set; }

        public long VideoSize { get; set; }

        public string Filename { get; set; }
    }

    /// <summary>
    /// Verbatim port of the pure subtitle helpers exported by
    /// js/data/repository/subtitleRepository.js.
    /// </summary>
    public static class SubtitleCandidates
    {
        // subtitleRepository.js:9-19.
        internal static string NormalizeMatchText(string value)
        {
            if (value == null)
            {
                return "";
            }
            return Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ").Trim();
        }

        private static readonly Regex YearRegex = new Regex(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
        private static readonly Regex ImdbIdRegex = new Regex("^tt\\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string ReleaseYear(string value)
        {
            var match = YearRegex.Match(value ?? "");
            return match.Success ? match.Value : "";
        }

        // subtitleRepository.js:21-46 selectCanonicalCinemetaId.
        public static string SelectCanonicalCinemetaId(
            IReadOnlyList<Meta> items, string type, string title, string year)
        {
            var rawType = (type ?? "").Trim().ToLowerInvariant();
            var normalizedType = rawType == "tv" ? "series" : rawType;
            var normalizedTitle = NormalizeMatchText(title);
            var normalizedYear = ReleaseYear(year);
            if (!(normalizedType == "movie" || normalizedType == "series") ||
                normalizedTitle.Length == 0 || normalizedYear.Length == 0)
            {
                return "";
            }

            var matches = (items ?? new List<Meta>()).Where(item =>
            {
                var rawItemType = (item?.Type ?? "").Trim().ToLowerInvariant();
                var itemType = rawItemType == "tv" ? "series" : rawItemType;
                return ImdbIdRegex.IsMatch((item?.Id ?? "").Trim()) &&
                       itemType == normalizedType &&
                       NormalizeMatchText(item?.Name) == normalizedTitle &&
                       ReleaseYear(item?.ReleaseInfo) == normalizedYear;
            }).ToList();

            var ids = matches
                .Select(match => (match.Id ?? "").Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return ids.Count == 1 ? ids[0] : "";
        }

        // subtitleRepository.js:48-99 buildSubtitleIdCandidates.
        public static IReadOnlyList<string> BuildSubtitleIdCandidates(
            string type,
            IEnumerable<string> ids = null,
            string videoId = null,
            int? season = null,
            int? episode = null,
            IEnumerable<string> idPrefixes = null)
        {
            var candidates = new List<string>();
            void Push(string value)
            {
                var normalized = (value ?? "").Trim();
                if (normalized.Length == 0 || candidates.Contains(normalized))
                {
                    return;
                }
                candidates.Add(normalized);
            }

            var normalizedType = (type ?? "").Trim().ToLowerInvariant();
            var idList = (ids ?? Enumerable.Empty<string>()).ToList();
            var prefixList = (idPrefixes ?? Enumerable.Empty<string>())
                .Select(prefix => (prefix ?? "").Trim())
                .Where(prefix => prefix.Length > 0)
                .ToList();

            if (normalizedType == "series")
            {
                Push(videoId);
                if (season.HasValue && season.Value > 0 && episode.HasValue && episode.Value > 0)
                {
                    foreach (var id in idList)
                    {
                        if (ImdbIdRegex.IsMatch((id ?? "").Trim()))
                        {
                            Push($"{id.Trim()}:{season.Value}:{episode.Value}");
                        }
                    }
                }
                if (candidates.Count == 0)
                {
                    foreach (var id in idList)
                    {
                        Push(id);
                    }
                }
            }
            else
            {
                foreach (var id in idList)
                {
                    Push(id);
                }
            }

            var compatible = prefixList.Count > 0
                ? candidates.Where(candidate => prefixList.Any(prefix => candidate.StartsWith(prefix, StringComparison.Ordinal))).ToList()
                : candidates;
            if (compatible.Count > 0)
            {
                return compatible;
            }
            return prefixList.Count > 0 ? new List<string>() : candidates;
        }
    }

    /// <summary>
    /// Verbatim port of js/data/repository/subtitleRepository.js class body:
    /// addon filtering by subtitles resource support, per-candidate-id fetching
    /// with a 20s per-request timeout, deterministic fallback ids, and
    /// url+lang dedupe. Addons are supplied by the caller (installed list).
    /// </summary>
    public sealed class SubtitleRepository
    {
        private const int PerAddonTimeoutMs = 20000;
        public const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";

        private static readonly Regex ImdbIdRegex = new Regex("^tt\\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly StremioAddonClient _client;
        private readonly CatalogRepository _catalogs;

        public SubtitleRepository(StremioAddonClient client, CatalogRepository catalogs)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        }
        public async Task<IReadOnlyList<SubtitleItem>> GetSubtitlesAsync(
            IReadOnlyList<Addon> installedAddons,
            string type, string id, string videoId = null,
            SubtitleRequestOptions options = null,
            CancellationToken ct = default)
        {
            options = options ?? new SubtitleRequestOptions();
            var normalizedType = CanonicalSubtitleType(type);
            var rawId = (id ?? "").Trim();
            var normalizedId = NormalizeIdForLookup(rawId);
            var canonicalId = ImdbIdRegex.IsMatch(normalizedId)
                ? normalizedId
                : await ResolveCanonicalIdAsync(normalizedType, options, ct).ConfigureAwait(false);
            var idCandidates = UniqueNonEmpty(new[] { canonicalId, normalizedId, rawId });

            var subtitleAddons = (installedAddons ?? Array.Empty<Addon>()).Where(addon =>
                (addon.Resources ?? new List<AddonResource>()).Any(resource =>
                    IsSubtitleResource(resource?.Name) &&
                    SupportsType(resource, normalizedType, normalizedId))).ToList();

            var allResults = await NuvioTV.Core.Networking.MapWithConcurrency.RunAsync(
                int.MaxValue, // JS: Promise.all over subtitleAddons — unbounded.
                subtitleAddons.Select(addon => addon).ToList(),
                (addon, innerCt) => FetchSubtitlesFromAddonAsync(addon, normalizedType, idCandidates, videoId, options),
                ct).ConfigureAwait(false);

            var mergedResults = new List<SubtitleItem>();
            foreach (var items in allResults)
            {
                mergedResults.AddRange(items);
            }
            return mergedResults;
        }

        // subtitleRepository.js:138-182 fetchSubtitlesFromAddon.
        public async Task<IReadOnlyList<SubtitleItem>> FetchSubtitlesFromAddonAsync(
            Addon addon, string type, IReadOnlyList<string> idCandidates,
            string videoId, SubtitleRequestOptions options = null)
        {
            options = options ?? new SubtitleRequestOptions();
            var candidateIds = SubtitleCandidates.BuildSubtitleIdCandidates(
                type, idCandidates, videoId, options.Season, options.Episode,
                addon?.IdPrefixes);
            if (candidateIds.Count == 0)
            {
                return new List<SubtitleItem>();
            }

            var merged = new List<SubtitleItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var actualId in candidateIds)
            {
                JsonElement payload;
                try
                {
                    using var timeoutCts = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(Math.Max(500, PerAddonTimeoutMs)));
                    payload = await _client.FetchSubtitlesAsync(
                        addon.BaseUrl, type, actualId,
                        options.VideoHash, options.VideoSize, options.Filename,
                        timeoutCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // js withTimeout → { status: "timeout" } / safeApiCall error → continue.
                    continue;
                }

                var subtitles = StremioJson.Elements(StremioJson.ArrayOrEmpty(payload, "subtitles"));
                foreach (var subtitleElement in subtitles)
                {
                    var url = StremioJson.StrOrNull(subtitleElement, "url");
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }
                    var lang = StremioJson.Str(subtitleElement, "lang");
                    var id = StremioJson.StrOrNull(subtitleElement, "id");
                    var key = $"{url}::{(string.IsNullOrEmpty(lang) ? "" : lang.ToLowerInvariant())}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }
                    merged.Add(new SubtitleItem(
                        string.IsNullOrEmpty(id)
                            ? $"{(string.IsNullOrEmpty(lang) ? "unk" : lang)}-{MakeDeterministicId(url)}"
                            : id,
                        url,
                        string.IsNullOrEmpty(lang) ? "unknown" : lang,
                        addon.DisplayName,
                        addon.Logo));
                }
            }

            return merged;
        }

        // subtitleRepository.js:184-231.
        public static bool IsSubtitleResource(string name)
        {
            var resourceName = (name ?? "").ToLowerInvariant();
            return resourceName == "subtitles" || resourceName == "subtitle";
        }

        public static string CanonicalSubtitleType(string type)
        {
            var normalized = (type ?? "").Trim().ToLowerInvariant();
            return normalized == "tv" ? "series" : normalized;
        }

        public static bool SupportsType(AddonResource resource, string type, string id)
        {
            var supportedTypes = ((resource?.Types ?? new List<string>()) ?? new List<string>())
                .Select(value => (value ?? "").Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .ToList();
            var compatibleTypes = CompatibleTypes(type);
            if (supportedTypes.Count > 0 &&
                !compatibleTypes.Any(candidateType => supportedTypes.Contains(candidateType)))
            {
                return false;
            }

            var idPrefixes = ((resource?.IdPrefixes ?? new List<string>()) ?? new List<string>())
                .Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0)
                .ToList();
            if (idPrefixes.Count == 0)
            {
                return true;
            }
            return idPrefixes.Any(prefix => (id ?? "").StartsWith(prefix, StringComparison.Ordinal));
        }

        public static string NormalizeIdForLookup(string id)
        {
            var raw = (id ?? "").Trim();
            if (raw.Length == 0)
            {
                return "";
            }
            var firstSegment = raw.Split(':')[0] ?? "";
            return firstSegment.Trim().Length > 0 ? firstSegment.Trim() : raw;
        }

        public static IReadOnlyList<string> CompatibleTypes(string type)
        {
            var normalized = CanonicalSubtitleType(type);
            if (normalized == "series" || normalized == "tv")
            {
                return new[] { "series", "tv" };
            }
            return new[] { normalized };
        }

        public static IReadOnlyList<string> UniqueNonEmpty(IEnumerable<string> values)
        {
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                var normalized = (value ?? "").Trim();
                if (normalized.Length == 0 || !seen.Add(normalized))
                {
                    continue;
                }
                unique.Add(normalized);
            }
            return unique;
        }

        // subtitleRepository.js:258-278 resolveCanonicalId — Cinemeta top catalog
        // search restricted to movie/series with title + year.
        private async Task<string> ResolveCanonicalIdAsync(
            string type, SubtitleRequestOptions options, CancellationToken ct)
        {
            var title = (options?.Title ?? "").Trim();
            var year = SubtitleCandidates.ReleaseYear(options?.Year);
            if (!(type == "movie" || type == "series") ||
                title.Length == 0 || year.Length == 0)
            {
                return "";
            }

            CatalogRow row;
            try
            {
                row = await _catalogs.GetAsync(
                    CinemetaBaseUrl, "org.cinemeta", "Cinemeta",
                    "top", "Cinemeta Search", type,
                    skip: 0,
                    extraArgs: new Dictionary<string, string> { ["search"] = title },
                    supportsSkip: false,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return "";
            }
            if (row == null)
            {
                return "";
            }
            return SubtitleCandidates.SelectCanonicalCinemetaId(row.Items, type, title, year);
        }

        // subtitleRepository.js:320-328 makeDeterministicId — JS 32-bit rolling hash.
        public static string MakeDeterministicId(string value)
        {
            unchecked
            {
                var hash = 0;
                var str = value ?? "";
                foreach (var c in str)
                {
                    hash = (hash << 5) - hash + c;
                }
                return Math.Abs((long)hash).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
