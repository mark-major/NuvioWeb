using System;
using System.Collections.Generic;
using System.Linq;

namespace NuvioTV.Core.Addons
{
    /// <summary>
    /// Stremio addon URL construction. Exact port of the webapp builders:
    /// - canonicalizeUrl / buildManifestUrl: js/data/repository/addonRepository.js:26-46
    /// - buildCatalogUrl: js/data/repository/catalogRepository.js:75-101
    /// - buildMetaUrl: js/data/repository/metaRepository.js:160-166
    /// - buildStreamUrl: js/data/repository/streamRepository.js:239-245
    /// - buildSubtitlesUrl + extra params: js/data/repository/subtitleRepository.js:301-348
    /// </summary>
    public static class AddonUrlBuilder
    {
        private const string ManifestSuffix = "/manifest.json";

        /// <summary>
        /// Strips trailing slashes and a trailing /manifest.json (path part only;
        /// any query string is preserved verbatim).
        /// </summary>
        public static string CanonicalizeUrl(string url)
        {
            var trimmed = (url ?? "").Trim();
            trimmed = trimmed.TrimEnd('/');

            var queryStart = trimmed.IndexOf('?');
            var path = queryStart >= 0 ? trimmed.Substring(0, queryStart) : trimmed;
            var query = queryStart >= 0 ? trimmed.Substring(queryStart) : "";

            var cleanPath = path.ToLowerInvariant().EndsWith(ManifestSuffix)
                ? path.Substring(0, path.Length - ManifestSuffix.Length).TrimEnd('/')
                : path.TrimEnd('/');
            return cleanPath + query;
        }

        public static string BuildManifestUrl(string baseUrl)
        {
            var cleanBaseUrl = CanonicalizeUrl(baseUrl);
            var queryStart = cleanBaseUrl.IndexOf('?');
            var basePath = queryStart >= 0
                ? cleanBaseUrl.Substring(0, queryStart).TrimEnd('/')
                : cleanBaseUrl;
            var baseQuery = queryStart >= 0 ? cleanBaseUrl.Substring(queryStart) : "";
            return basePath + "/manifest.json" + baseQuery;
        }

        // JS parity (catalogRepository.js:75-101): no extra args → skip lives in the
        // path segment; with extra args everything moves into the path query segment.
        public static string BuildCatalogUrl(
            string baseUrl, string type, string catalogId, int skip = 0,
            IReadOnlyDictionary<string, string> extraArgs = null)
        {
            SplitBase(baseUrl, out var basePath, out var baseQuery);
            var args = new Dictionary<string, string>();
            if (extraArgs != null)
            {
                foreach (var pair in extraArgs)
                {
                    args[pair.Key] = pair.Value;
                }
            }

            if (args.Count == 0)
            {
                return skip > 0
                    ? basePath + "/catalog/" + type + "/" + catalogId + "/skip=" + skip + ".json" + baseQuery
                    : basePath + "/catalog/" + type + "/" + catalogId + ".json" + baseQuery;
            }

            if (skip > 0 && !args.ContainsKey("skip"))
            {
                args["skip"] = skip.ToString();
            }

            var query = string.Join("&", args.Select(pair =>
                EncodeArg(pair.Key) + "=" + EncodeArg(pair.Value)));

            return basePath + "/catalog/" + type + "/" + catalogId + "/" + query + ".json" + baseQuery;
        }

        public static string BuildMetaUrl(string baseUrl, string type, string id)
        {
            SplitBase(baseUrl, out var basePath, out var baseQuery);
            return basePath + "/meta/" + Encode(type) + "/" + Encode(id) + ".json" + baseQuery;
        }

        public static string BuildStreamUrl(string baseUrl, string type, string videoId)
        {
            SplitBase(baseUrl, out var basePath, out var baseQuery);
            return basePath + "/stream/" + Encode(type) + "/" + Encode(videoId) + ".json" + baseQuery;
        }

        public static string BuildSubtitlesUrl(
            string baseUrl, string type, string id,
            string videoHash = null, long videoSize = 0, string filename = null)
        {
            SplitBase(baseUrl, out var basePath, out var baseQuery);
            var extraParams = BuildSubtitleExtraParams(videoHash, videoSize, filename);
            var suffix = extraParams.Length > 0 ? "/" + extraParams : "";
            return basePath + "/subtitles/" + Encode(type) + "/" + EncodeSubtitleId(id) + suffix + ".json" + baseQuery;
        }

        // JS parity (subtitleRepository.js:331-348): only non-empty values; videoSize
        // only when > 0; keys are NOT encoded (plain identifiers).
        public static string BuildSubtitleExtraParams(string videoHash, long videoSize, string filename)
        {
            var parts = new List<string>();
            Push(parts, "videoHash", videoHash);
            if (videoSize > 0)
            {
                parts.Add("videoSize=" + videoSize.ToString());
            }
            Push(parts, "filename", filename);
            return string.Join("&", parts);
        }

        private static void Push(List<string> parts, string key, string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.Length > 0)
            {
                parts.Add(key + "=" + EncodeArg(normalized));
            }
        }

        private static void SplitBase(string baseUrl, out string basePath, out string baseQuery)
        {
            var cleanBaseUrl = CanonicalizeUrl(baseUrl);
            var queryStart = cleanBaseUrl.IndexOf('?');
            basePath = queryStart >= 0
                ? cleanBaseUrl.Substring(0, queryStart).TrimEnd('/')
                : cleanBaseUrl;
            baseQuery = queryStart >= 0 ? cleanBaseUrl.Substring(queryStart) : "";
        }

        // JS parity: encodeURIComponent then '+' → '%20'.
        public static string EncodeArg(string value)
        {
            return Uri.EscapeDataString(value ?? "").Replace("+", "%20");
        }

        public static string Encode(string value)
        {
            return EncodeArg(value);
        }

        // JS parity (subtitleRepository.js:325-330): ':' stays literal in subtitle ids.
        public static string EncodeSubtitleId(string value)
        {
            return EncodeArg(value).Replace("%3A", ":").Replace("%3a", ":");
        }
    }
}
