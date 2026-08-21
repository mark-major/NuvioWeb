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
    /// <summary>
    /// Shared JSON accessors for the Stremio repository ports (catalog/meta/
    /// stream/subtitle). Mirrors the defensive property reads used throughout
    /// the JS repository layer.
    /// </summary>
    internal static class StremioJson
    {
        public static bool TryGet(JsonElement element, string name, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            return element.TryGetProperty(name, out value);
        }

        /// <summary>String property with "" default; JSON null and non-strings become "".</summary>
        public static string Str(JsonElement element, string name)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return "";
            }
            return value.GetString() ?? "";
        }

        /// <summary>String property preserved as null when absent/null (JS `|| null` slots).</summary>
        public static string StrOrNull(JsonElement element, string name)
        {
            if (!TryGet(element, name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return value.GetString();
        }

        public static JsonElement ArrayOrEmpty(JsonElement element, string name)
        {
            if (TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
            return default;
        }

        public static List<JsonElement> Elements(JsonElement array)
        {
            var items = new List<JsonElement>();
            if (array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    items.Add(item);
                }
            }
            return items;
        }

        public static T? NullableInt<T>(JsonElement element, string name)
            where T : struct
        {
            if (!TryGet(element, name, out var value))
            {
                return null;
            }
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    return (T)Convert.ChangeType(value.GetDouble(), typeof(T));
                case JsonValueKind.String:
                    if (double.TryParse(value.GetString(), out var parsed))
                    {
                        return (T)Convert.ChangeType(parsed, typeof(T));
                    }
                    return null;
                default:
                    return null;
            }
        }

        // metaRepository.js:5-9 normalizeDisplayText.
        public static string NormalizeDisplayText(string value)
        {
            return (value ?? "").Replace("\\'", "'").Replace("\\\"", "\"");
        }

        public static T Deserialize<T>(JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        }
    }

    /// <summary>
    /// Catalog row fetcher. Verbatim port of js/data/repository/catalogRepository.js:
    /// per-(addon,type,catalog,skip,args) row cache, URL construction delegated to
    /// AddonUrlBuilder.BuildCatalogUrl, metas mapped onto Models.Meta with addon
    /// identity stamped on the row, skip paging flags per JS.
    /// </summary>
    public sealed class CatalogRepository
    {
        private readonly StremioAddonClient _client;
        private readonly object _lock = new object();
        private readonly Dictionary<string, CatalogRow> _catalogCache = new Dictionary<string, CatalogRow>();

        public CatalogRepository(StremioAddonClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// js getCatalog({addonBaseUrl, addonId, addonName, catalogId, catalogName,
        /// type, skip, extraArgs, supportsSkip}). Returns the assembled row or
        /// throws on transport failure (js safeApiCall maps that to status:"error").
        /// </summary>
        public async Task<CatalogRow> GetAsync(
            string addonBaseUrl, string addonId, string addonName,
            string catalogId, string catalogName, string type,
            int skip = 0, IReadOnlyDictionary<string, string> extraArgs = null,
            bool supportsSkip = true, CancellationToken ct = default)
        {
            var cacheKey = BuildCacheKey(addonId, type, catalogId, skip, extraArgs);
            lock (_lock)
            {
                if (_catalogCache.TryGetValue(cacheKey, out var cachedRow))
                {
                    return cachedRow;
                }
            }

            var payload = await _client.FetchCatalogAsync(
                addonBaseUrl, type, catalogId, skip, extraArgs, ct).ConfigureAwait(false);

            var metas = StremioJson.ArrayOrEmpty(payload, "metas");
            var items = StremioJson.Elements(metas)
                .Select(MapMeta)
                .ToList();

            var row = new CatalogRow(
                addonId,
                addonName,
                addonBaseUrl,
                catalogId,
                catalogName,
                type,
                items,
                isLoading: false,
                hasMore: supportsSkip && items.Count > 0,
                currentPage: skip / 100,
                supportsSkip: supportsSkip);

            lock (_lock)
            {
                _catalogCache[cacheKey] = row;
            }
            return row;
        }

        /// <summary>Brief seam: GetRowAsync(addon, type, catalogId, skip, extraSearchParams).</summary>
        public Task<CatalogRow> GetRowAsync(
            Addon addon, string type, string catalogId,
            int skip = 0, IReadOnlyDictionary<string, string> extraArgs = null,
            bool supportsSkip = true, CancellationToken ct = default)
        {
            if (addon == null)
            {
                throw new ArgumentNullException(nameof(addon));
            }

            var catalogName = ResolveCatalogName(addon, type, catalogId);
            return GetAsync(
                addon.BaseUrl,
                addon.Id,
                addon.DisplayName,
                catalogId,
                catalogName,
                type,
                skip,
                extraArgs,
                supportsSkip,
                ct);
        }

        // catalogRepository.js:101-108 — args sorted by key, joined "key=value".
        public static string BuildCacheKey(
            string addonId, string type, string catalogId, int skip = 0,
            IReadOnlyDictionary<string, string> extraArgs = null)
        {
            var normalizedArgs = (extraArgs ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}");
            return $"{addonId}_{type}_{catalogId}_{skip}_{string.Join("&", normalizedArgs)}";
        }

        // catalogRepository.js:114-126 mapMeta — no escape normalization here.
        internal static Meta MapMeta(JsonElement meta)
        {
            var genres = StremioJson.Elements(StremioJson.ArrayOrEmpty(meta, "genres"))
                .Select(genre => genre.ValueKind == JsonValueKind.String ? genre.GetString() : "")
                .ToList();

            var videos = new List<MetaVideo>();
            foreach (var video in StremioJson.Elements(StremioJson.ArrayOrEmpty(meta, "videos")))
            {
                videos.Add(StremioJson.Deserialize<MetaVideo>(video));
            }

            var name = StremioJson.Str(meta, "name");
            var description = StremioJson.Str(meta, "description");
            var releaseInfo = StremioJson.Str(meta, "releaseInfo");

            return new Meta(
                StremioJson.Str(meta, "id"),
                StremioJson.Str(meta, "type"),
                string.IsNullOrEmpty(name) ? "Untitled" : name,
                OrNull(StremioJson.StrOrNull(meta, "poster")),
                OrNull(StremioJson.StrOrNull(meta, "background")),
                OrNull(StremioJson.StrOrNull(meta, "logo")),
                description,
                genres,
                videos,
                releaseInfo);
        }

        private static string OrNull(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string ResolveCatalogName(Addon addon, string type, string catalogId)
        {
            foreach (var catalog in addon.Catalogs ?? new List<AddonCatalog>())
            {
                var apiType = !string.IsNullOrEmpty(catalog?.ApiType)
                    ? catalog.ApiType
                    : catalog?.Type;
                if (string.Equals(catalog?.Id, catalogId, StringComparison.Ordinal) &&
                    string.Equals(apiType, type, StringComparison.Ordinal))
                {
                    return string.IsNullOrEmpty(catalog.Name) ? catalogId : catalog.Name;
                }
            }
            return catalogId;
        }
    }
}
