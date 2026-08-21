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
    /// IMDb episode ratings. Port of js/data/repository/imdbEpisodeRatingsRepository.js:
    /// primary tapframe source keyed by imdb id, fallback ratings source keyed by
    /// tmdb id; payload mapping produces seasons of {episode, rating} sorted by
    /// episode with ratings rounded to one decimal.
    /// </summary>
    public sealed class ImdbEpisodeRatingsClient
    {
        private readonly HttpClient _httpClient;

        public ImdbEpisodeRatingsClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<IReadOnlyDictionary<int, IReadOnlyList<EpisodeRating>>> GetEpisodeRatingsAsync(
            string imdbId, long tmdbId, CancellationToken ct = default)
        {
            var normalizedImdbId = NormalizeImdbId(imdbId);
            if (!string.IsNullOrEmpty(normalizedImdbId))
            {
                var url = BuildSeasonRatingsUrl(AppConfig.ImdbTapframeApiBaseUrl, normalizedImdbId);
                var payload = await FetchArrayAsync(url, ct).ConfigureAwait(false);
                var mapped = MapRatingsPayload(payload ?? default);
                if (mapped.Count > 0)
                {
                    return mapped;
                }
            }

            if (tmdbId > 0)
            {
                var url = BuildSeasonRatingsUrl(AppConfig.ImdbRatingsApiBaseUrl, tmdbId.ToString());
                var payload = await FetchArrayAsync(url, ct).ConfigureAwait(false);
                return MapRatingsPayload(payload ?? default);
            }

            return EmptySeasons();
        }

        public static string BuildSeasonRatingsUrl(string baseUrl, string id)
        {
            var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
            if (normalizedBaseUrl.Length == 0 || string.IsNullOrEmpty(id))
            {
                return null;
            }
            return normalizedBaseUrl + "api/shows/" + Uri.EscapeDataString(id) + "/season-ratings";
        }

        // JS parity mapRatingsPayload (imdbEpisodeRatingsRepository.js:44-70).
        public static IReadOnlyDictionary<int, IReadOnlyList<EpisodeRating>> MapRatingsPayload(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Array)
            {
                return EmptySeasons();
            }

            var seasons = new SortedDictionary<int, List<EpisodeRating>>();
            foreach (var seasonEntry in payload.EnumerateArray())
            {
                if (seasonEntry.ValueKind != JsonValueKind.Object ||
                    !seasonEntry.TryGetProperty("episodes", out var episodes) ||
                    episodes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var episodeEntry in episodes.EnumerateArray())
                {
                    var seasonNumber = GetNumber(episodeEntry, "season_number");
                    var episodeNumber = GetNumber(episodeEntry, "episode_number");
                    var ratingValue = GetNumber(episodeEntry, "vote_average");
                    if (seasonNumber < 0 || episodeNumber <= 0 || double.IsNaN(ratingValue))
                    {
                        continue;
                    }
                    if (!seasons.TryGetValue((int)seasonNumber, out var list))
                    {
                        list = new List<EpisodeRating>();
                        seasons[(int)seasonNumber] = list;
                    }
                    // JS parity: Number(x.toFixed(1)) rounds halves away from zero.
                    list.Add(new EpisodeRating((int)episodeNumber, Math.Round(ratingValue, 1, MidpointRounding.AwayFromZero)));
                }
            }

            var result = new Dictionary<int, IReadOnlyList<EpisodeRating>>();
            foreach (var pair in seasons)
            {
                result[pair.Key] = pair.Value.OrderBy(e => e.Episode).ToList();
            }
            return result;
        }

        private async Task<JsonElement?> FetchArrayAsync(string url, CancellationToken ct)
        {
            if (url == null)
            {
                return null;
            }
            using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static double GetNumber(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.Number)
            {
                return double.NaN;
            }
            return value.GetDouble();
        }

        public static string NormalizeImdbId(string value)
        {
            var candidate = (value ?? "").Trim().Split(':')[0];
            return candidate.StartsWith("tt") && candidate.Length > 2 &&
                   candidate.Substring(2).All(char.IsDigit)
                ? candidate
                : "";
        }

        public static string NormalizeBaseUrl(string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.Length == 0)
            {
                return "";
            }
            return normalized.EndsWith("/") ? normalized : normalized + "/";
        }

        private static IReadOnlyDictionary<int, IReadOnlyList<EpisodeRating>> EmptySeasons()
        {
            return new Dictionary<int, IReadOnlyList<EpisodeRating>>();
        }
    }

    public readonly struct EpisodeRating
    {
        public int Episode { get; }
        public double Rating { get; }

        public EpisodeRating(int episode, double rating)
        {
            Episode = episode;
            Rating = rating;
        }
    }
}
