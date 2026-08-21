using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Integrations.Ratings
{
    /// <summary>
    /// MDBList provider ratings. Port of js/data/repository/mdbListRepository.js
    /// fetchRatings: one GET per enabled provider at /rating/{mediaType}/{provider}
    /// with concurrency 4; results keyed by provider key (null when unavailable).
    /// </summary>
    public sealed class MdbListClient
    {
        public static readonly IReadOnlyDictionary<string, string> Providers =
            new Dictionary<string, string>
            {
                ["trakt"] = "trakt",
                ["imdb"] = "imdb",
                ["tmdb"] = "tmdb",
                ["letterboxd"] = "letterboxd",
                ["tomatoes"] = "tomatoes",
                ["audience"] = "audience",
                ["metacritic"] = "metacritic",
                ["mal"] = "mal"
            };

        private readonly HttpClient _httpClient;

        public MdbListClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        // JS parity normalizeMediaType: movie|film → movie; series/tv/show/tvshow → show.
        public static string NormalizeMediaType(string rawType)
        {
            switch ((rawType ?? "").Trim().ToLowerInvariant())
            {
                case "series":
                case "tv":
                case "show":
                case "tvshow":
                    return "show";
                default:
                    return "movie";
            }
        }

        public static string ExtractImdbId(string rawId)
        {
            var match = System.Text.RegularExpressions.Regex.Match(rawId ?? "", @"tt\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Value : null;
        }

        public static string ExtractTmdbId(string rawId)
        {
            var trimmed = (rawId ?? "").Trim();
            if (trimmed.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed.Substring(5).Split(':')[0];
                return value.All(char.IsDigit) && value.Length > 0 ? value : null;
            }
            return null;
        }

        public async Task<IReadOnlyDictionary<string, double?>> FetchRatingsAsync(
            string rawId, string mediaType, IEnumerable<string> enabledProviders, CancellationToken ct = default)
        {
            var normalizedType = NormalizeMediaType(mediaType);
            var imdbId = ExtractImdbId(rawId) ?? ExtractTmdbId(rawId);
            var requestBody = new { id = imdbId ?? "" };
            var apiKey = AppConfig.TmdbApiKey; // JS uses the same TMDB api key for mdblist

            var providers = (enabledProviders ?? Array.Empty<string>())
                .Select(p => p.Trim().ToLowerInvariant())
                .Where(p => Providers.ContainsKey(p))
                .ToList();

            var pairs = await MapWithConcurrency.RunAsync(
                4,
                providers,
                async (provider, innerCt) =>
                {
                    var rating = await FetchProviderRatingAsync(
                        normalizedType, provider, apiKey, requestBody, innerCt).ConfigureAwait(false);
                    return new KeyValuePair<string, double?>(provider, rating);
                },
                ct).ConfigureAwait(false);

            return pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        private async Task<double?> FetchProviderRatingAsync(
            string mediaType, string provider, string apiKey, object requestBody, CancellationToken ct)
        {
            var baseUrl = (AppConfig.MdbListApiBaseUrl ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0 || string.IsNullOrEmpty(apiKey))
            {
                return null;
            }
            var url = baseUrl + "/rating/" + Uri.EscapeDataString(mediaType) + "/" +
                      Uri.EscapeDataString(provider) + "?apikey=" + Uri.EscapeDataString(apiKey);
            try
            {
                using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var rating = root.ValueKind == JsonValueKind.Object &&
                             root.TryGetProperty("rating", out var value) &&
                             value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : double.NaN;
                return double.IsNaN(rating) ? (double?)null : rating;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
