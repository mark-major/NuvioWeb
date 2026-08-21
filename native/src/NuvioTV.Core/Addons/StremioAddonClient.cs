using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Addons
{
    /// <summary>
    /// HTTP client for Stremio addon protocol resources. All addon calls are
    /// unauthenticated (includeSessionAuth: false) — addons never see Supabase tokens.
    /// Defaults mirror DEFAULT_ADDON_URLS (addonRepository.js:12).
    /// </summary>
    public sealed class StremioAddonClient
    {
        public const string DefaultCinemetaUrl = "https://v3-cinemeta.strem.io";
        public const string DefaultOpensubtitlesUrl = "https://opensubtitles-v3.strem.io";

        private static readonly string[] DefaultAddonUrls =
        {
            DefaultCinemetaUrl,
            DefaultOpensubtitlesUrl
        };

        private readonly NuvioHttpClient _http;

        public StremioAddonClient(NuvioHttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public static IReadOnlyList<string> DefaultUrls => DefaultAddonUrls;

        public Task<AddonManifest> FetchManifestAsync(string baseUrl, CancellationToken ct = default)
        {
            return _http.GetJsonAsync<AddonManifest>(
                AddonUrlBuilder.BuildManifestUrl(baseUrl), includeSessionAuth: false, ct);
        }

        public Task<JsonElement> FetchCatalogAsync(
            string baseUrl, string type, string catalogId, int skip = 0,
            IReadOnlyDictionary<string, string> extraArgs = null, CancellationToken ct = default)
        {
            return _http.GetJsonAsync<JsonElement>(
                AddonUrlBuilder.BuildCatalogUrl(baseUrl, type, catalogId, skip, extraArgs),
                includeSessionAuth: false, ct);
        }

        public Task<JsonElement> FetchMetaAsync(string baseUrl, string type, string id, CancellationToken ct = default)
        {
            return _http.GetJsonAsync<JsonElement>(
                AddonUrlBuilder.BuildMetaUrl(baseUrl, type, id), includeSessionAuth: false, ct);
        }

        public Task<JsonElement> FetchStreamsAsync(string baseUrl, string type, string videoId, CancellationToken ct = default)
        {
            return _http.GetJsonAsync<JsonElement>(
                AddonUrlBuilder.BuildStreamUrl(baseUrl, type, videoId), includeSessionAuth: false, ct);
        }

        public Task<JsonElement> FetchSubtitlesAsync(
            string baseUrl, string type, string id,
            string videoHash = null, long videoSize = 0, string filename = null, CancellationToken ct = default)
        {
            return _http.GetJsonAsync<JsonElement>(
                AddonUrlBuilder.BuildSubtitlesUrl(baseUrl, type, id, videoHash, videoSize, filename),
                includeSessionAuth: false, ct);
        }
    }
}
