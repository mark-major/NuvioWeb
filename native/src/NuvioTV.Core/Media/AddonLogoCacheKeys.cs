using System;
using System.Collections.Generic;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// Storage keys and normalization helpers for the addon logo cache. Port of
    /// the key/limit surface of js/core/media/addonLogoCache.js (with
    /// js/core/media/imageProxy.js normalizeImageUrl). Rendering/decode paths are
    /// out of scope here; on Tizen the webOS image proxy is inactive, so
    /// NormalizeUrl is a plain trim — exactly what proxifyImageUrl returns there.
    /// </summary>
    public static class AddonLogoCacheKeys
    {
        /// <summary>LocalStore persistence key (addonLogoCache.js:8).</summary>
        public const string StorageKey = "nuvio.stream.addonLogoCache.v1";

        /// <summary>Default cache entry limit (addonLogoCache.js:9).</summary>
        public const int CacheLimit = 36;

        /// <summary>TV cache entry limit (addonLogoCache.js:10).</summary>
        public const int TvCacheLimit = 12;

        /// <summary>Max serialized cache payload length before reset (addonLogoCache.js:11).</summary>
        public const int MaxSerializedLength = 140000;

        // addonLogoCache.js:18-20 + imageProxy.js:148-166 — off-webOS this is trim-only.
        public static string NormalizeUrl(string value)
        {
            return (value ?? "").Trim();
        }

        // addonLogoCache.js:267-271 normalizeAddonLookupKey.
        public static string NormalizeLookupKey(string value)
        {
            return (value ?? "").Trim().ToLowerInvariant();
        }

        // addonLogoCache.js:273-280 rememberAddonLogoLookup.
        public static void RememberAddonLogoLookup(
            IDictionary<string, string> lookup, string addonName, string addonLogo)
        {
            if (lookup == null)
            {
                return;
            }
            var key = NormalizeLookupKey(addonName);
            var rawLogo = (addonLogo ?? "").Trim();
            var logo = NormalizeUrl(rawLogo);
            if (string.IsNullOrEmpty(logo))
            {
                logo = rawLogo;
            }
            if (key.Length > 0 && logo.Length > 0)
            {
                lookup[key] = logo;
            }
        }

        // addonLogoCache.js:282-288 normalizeAddonLogoLookup.
        public static Dictionary<string, string> NormalizeAddonLogoLookup(
            IReadOnlyDictionary<string, string> lookup)
        {
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            if (lookup == null)
            {
                return normalized;
            }
            foreach (var pair in lookup)
            {
                RememberAddonLogoLookup(normalized, pair.Key, pair.Value);
            }
            return normalized;
        }

        // addonLogoCache.js:290-293 resolveAddonLogo.
        public static string ResolveAddonLogo(string addonName, IReadOnlyDictionary<string, string> lookup)
        {
            var key = NormalizeLookupKey(addonName);
            if (key.Length == 0 || lookup == null || !lookup.TryGetValue(key, out var logo))
            {
                return "";
            }
            return NormalizeUrl(logo);
        }

        // addonLogoCache.js:295-309 failed-url set helpers.
        public static string FailedKeyFor(string url)
        {
            var normalized = NormalizeUrl(url);
            return normalized.Length > 0 ? normalized : null;
        }
    }
}
