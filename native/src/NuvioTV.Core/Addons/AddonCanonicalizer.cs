using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Addons
{
    /// <summary>
    /// Wire DTO for a Stremio addon manifest (manifest.json). Field names follow the
    /// Stremio manifest spec; the webapp reads exactly these (addonRepository.js
    /// getBuiltinFallbackManifest shape).
    /// </summary>
    public sealed class AddonManifest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("logo")]
        public string Logo { get; set; }

        [JsonPropertyName("background")]
        public string Background { get; set; }

        [JsonPropertyName("types")]
        public IReadOnlyList<string> Types { get; set; }

        [JsonPropertyName("resources")]
        public IReadOnlyList<AddonManifestResource> Resources { get; set; }

        [JsonPropertyName("catalogs")]
        public IReadOnlyList<AddonManifestCatalog> Catalogs { get; set; }
    }

    public sealed class AddonManifestResource
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("types")]
        public IReadOnlyList<string> Types { get; set; }

        [JsonPropertyName("idPrefixes")]
        public IReadOnlyList<string> IdPrefixes { get; set; }
    }

    public sealed class AddonManifestCatalog
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("extra")]
        public IReadOnlyList<AddonManifestExtra> Extra { get; set; }
    }

    public sealed class AddonManifestExtra
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// Cinemeta URL migration + builtin fallback manifest.
    /// JS parity: addonRepository.js normalizeCinemetaUrl (764-768) and
    /// getBuiltinFallbackManifest (770-794).
    /// </summary>
    public static class AddonCanonicalizer
    {
        public const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";

        public static string NormalizeCinemetaUrl(string url)
        {
            if (url == null)
            {
                return "";
            }
            var lower = url.ToLowerInvariant();
            var idx = lower.IndexOf("http://cinemeta-v3.strem.io", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return url.Substring(0, idx) + "https://v3-cinemeta.strem.io" +
                       url.Substring(idx + "http://cinemeta-v3.strem.io".Length);
            }
            idx = lower.IndexOf("https://cinemeta-v3.strem.io", StringComparison.Ordinal);
            if (idx >= 0)
            {
                return url.Substring(0, idx) + "https://v3-cinemeta.strem.io" +
                       url.Substring(idx + "https://cinemeta-v3.strem.io".Length);
            }
            return url;
        }

        // Verbatim port of the webapp's builtin fallback manifest JSON.
        public static AddonManifest GetBuiltinFallbackManifest(string baseUrl)
        {
            if (!string.Equals(AddonUrlBuilder.CanonicalizeUrl(baseUrl), CinemetaBaseUrl,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new AddonManifest
            {
                Id = "org.cinemeta",
                Name = "Cinemeta",
                Version = "fallback",
                Description = "Fallback Cinemeta manifest",
                Logo = null,
                Types = new[] { "movie", "series" },
                Resources = new[]
                {
                    new AddonManifestResource
                    {
                        Name = "catalog",
                        Types = new[] { "movie", "series" },
                        IdPrefixes = null
                    },
                    new AddonManifestResource
                    {
                        Name = "meta",
                        Types = new[] { "movie", "series" },
                        IdPrefixes = null
                    }
                },
                Catalogs = new[]
                {
                    new AddonManifestCatalog { Id = "top", Name = "Top Movies", Type = "movie", Extra = Array.Empty<AddonManifestExtra>() },
                    new AddonManifestCatalog { Id = "top", Name = "Top Series", Type = "series", Extra = Array.Empty<AddonManifestExtra>() }
                }
            };
        }
    }
}
