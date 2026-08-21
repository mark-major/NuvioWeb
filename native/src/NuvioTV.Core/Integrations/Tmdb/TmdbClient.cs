using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;

namespace NuvioTV.Core.Integrations.Tmdb
{
    /// <summary>
    /// TMDB REST client (js/core/tmdb/tmdbService.js + castDetailScreen.js
    /// endpoints). JSON layer matches the webapp byte-for-byte where synced.
    /// </summary>
    public sealed class TmdbClient
    {
        public const string BaseUrl = "https://api.themoviedb.org/3";

        private readonly HttpClient _http;

        public TmdbClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        // ---- response models ----

        public sealed class FindResult
        {
            [JsonPropertyName("movie_results")]
            public List<IdResult> MovieResults { get; set; } = new List<IdResult>();

            [JsonPropertyName("tv_results")]
            public List<IdResult> TvResults { get; set; } = new List<IdResult>();
        }

        public sealed class IdResult
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }
        }

        public sealed class ExternalIds
        {
            [JsonPropertyName("imdb_id")]
            public string ImdbId { get; set; }

            [JsonPropertyName("tvdb_id")]
            public int? TvdbId { get; set; }
        }

        public sealed class Person
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("biography")]
            public string Biography { get; set; }

            [JsonPropertyName("birthday")]
            public string Birthday { get; set; }

            [JsonPropertyName("place_of_birth")]
            public string PlaceOfBirth { get; set; }

            [JsonPropertyName("profile_path")]
            public string ProfilePath { get; set; }

            [JsonPropertyName("combined_credits")]
            public CombinedCredits Credits { get; set; }

            [JsonPropertyName("images")]
            public PersonImages Images { get; set; }
        }

        public sealed class CombinedCredits
        {
            [JsonPropertyName("cast")]
            public List<CreditEntry> Cast { get; set; } = new List<CreditEntry>();
        }

        public sealed class PersonImages
        {
            [JsonPropertyName("profiles")]
            public List<ImageEntry> Profiles { get; set; } = new List<ImageEntry>();
        }

        public sealed class ImageEntry
        {
            [JsonPropertyName("file_path")]
            public string FilePath { get; set; }
        }

        public sealed class CreditEntry
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("media_type")]
            public string MediaType { get; set; }

            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("poster_path")]
            public string PosterPath { get; set; }

            [JsonPropertyName("backdrop_path")]
            public string BackdropPath { get; set; }

            [JsonPropertyName("character")]
            public string Character { get; set; }

            [JsonPropertyName("release_date")]
            public string ReleaseDate { get; set; }

            [JsonPropertyName("first_air_date")]
            public string FirstAirDate { get; set; }

            [JsonPropertyName("vote_average")]
            public double VoteAverage { get; set; }
        }

        public sealed class SearchPersonResponse
        {
            [JsonPropertyName("results")]
            public List<PersonSearchEntry> Results { get; set; } = new List<PersonSearchEntry>();
        }

        public sealed class PersonSearchEntry
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("profile_path")]
            public string ProfilePath { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        // ---- endpoints ----

        public async Task<FindResult> FindByImdbAsync(string imdbId, string apiKey, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id&api_key={Uri.EscapeDataString(apiKey)}";
            return await GetJsonAsync<FindResult>(url, ct);
        }

        public async Task<ExternalIds> ExternalIdsAsync(string contentType, string id, string apiKey, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/{contentType}/{Uri.EscapeDataString(id)}/external_ids?api_key={Uri.EscapeDataString(apiKey)}";
            return await GetJsonAsync<ExternalIds>(url, ct);
        }

        public async Task<List<PersonSearchEntry>> SearchPersonAsync(string name, string apiKey, string language = "en-US", CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/search/person?api_key={Uri.EscapeDataString(apiKey)}" +
                      $"&language={Uri.EscapeDataString(language ?? "en-US")}&query={Uri.EscapeDataString(name)}";
            var response = await GetJsonAsync<SearchPersonResponse>(url, ct);
            return response?.Results ?? new List<PersonSearchEntry>();
        }

        public async Task<Person> PersonAsync(int personId, string apiKey, string language = "en-US", bool includeCredits = true, CancellationToken ct = default)
        {
            var append = includeCredits ? "&append_to_response=combined_credits,images" : "";
            var url = $"{BaseUrl}/person/{personId}?api_key={Uri.EscapeDataString(apiKey)}" +
                      $"&language={Uri.EscapeDataString(language ?? "en-US")}{append}";
            return await GetJsonAsync<Person>(url, ct);
        }

        /// <summary>Image URL helper (w500 profile/poster scale).</summary>
        public static string ImageUrl(string path, string size = "w500")
        {
            if (string.IsNullOrEmpty(path)) return null;
            return $"https://image.tmdb.org/t/p/{size}{path}";
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
        {
            using (var response = await _http.GetAsync(url, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"TMDB {(int)response.StatusCode} for {url}");
                }
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var task = JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
                    return task.Result;
                }
            }
        }
    }
}
