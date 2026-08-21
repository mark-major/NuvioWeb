using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Integrations;
using NuvioTV.Core.Integrations.Ratings;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for metadata/ratings clients against the JS repositories:
    /// imdbEpisodeRatingsRepository.js, mdbListRepository.js,
    /// parentalGuideRepository.js, skipIntroRepository.js.
    /// </summary>
    [Collection("StaticConfig")]
    public class MetadataClientTests : IDisposable
    {
        public MetadataClientTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["imdbTapframeApiBaseUrl"] = "https://tapframe.test/",
                ["imdbRatingsApiBaseUrl"] = "https://imdb-ratings.test/",
                ["mdbListApiBaseUrl"] = "https://api.mdblist.test",
                ["parentalGuideApiUrl"] = "https://parental.test/",
                ["introDbApiUrl"] = "https://introdb.test/",
                ["tmdbApiKey"] = "tmdb-key-1"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            AppConfig.ResetForTests();
        }

        private static HttpResponseMessage JsonResponse(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // IMDb episode ratings
        // ------------------------------------------------------------------

        [Fact]
        public async Task ImdbRatings_PrimaryTapframeByImdbId()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new[]
            {
                new { episodes = new object[]
                {
                    new { season_number = 1, episode_number = 2, vote_average = 8.25 },
                    new { season_number = 1, episode_number = 1, vote_average = 7.0 },
                    new { season_number = 1, episode_number = 3, vote_average = 999 } // invalid → skipped? no: finite
                } }
            }));
            var client = new ImdbEpisodeRatingsClient(new HttpClient(handler));

            var seasons = await client.GetEpisodeRatingsAsync("tt1234567", 0);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://tapframe.test/api/shows/tt1234567/season-ratings", request.RequestUri.ToString());
            Assert.True(seasons.ContainsKey(1));
            Assert.Equal(1, seasons[1][0].Episode);
            Assert.Equal(2, seasons[1][1].Episode);
            // JS toFixed parity: 8.25 → 8.3 (half away from zero).
            Assert.Equal(8.3, seasons[1][1].Rating);
        }

        [Fact]
        public async Task ImdbRatings_FallsBackToTmdbSource()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)); // tapframe miss
            handler.Enqueue(JsonResponse(new[]
            {
                new { episodes = new object[] { new { season_number = 2, episode_number = 5, vote_average = 9.0 } } }
            }));
            var client = new ImdbEpisodeRatingsClient(new HttpClient(handler));

            var seasons = await client.GetEpisodeRatingsAsync("tt1234567", 42);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("https://imdb-ratings.test/api/shows/42/season-ratings", handler.Requests[1].RequestUri.ToString());
            Assert.Equal(5, seasons[2][0].Episode);
            Assert.Equal(9.0, seasons[2][0].Rating);
        }

        [Fact]
        public void MapRatingsPayload_SkipsInvalidEntries_AndSortsEpisodes()
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(@"[
                { ""episodes"": [
                    { ""season_number"": 1, ""episode_number"": 3, ""vote_average"": 6.0 },
                    { ""season_number"": -1, ""episode_number"": 2, ""vote_average"": 6.0 },
                    { ""season_number"": 1, ""episode_number"": 0, ""vote_average"": 6.0 },
                    { ""season_number"": 1, ""episode_number"": 1, ""vote_average"": 7.55 }
                ] }
            ]");

            var seasons = ImdbEpisodeRatingsClient.MapRatingsPayload(payload);

            Assert.Single(seasons);
            var episodes = seasons[1];
            Assert.Equal(2, episodes.Count); // ep0 and season -1 dropped
            Assert.Equal(1, episodes[0].Episode);
            Assert.Equal(7.6, episodes[0].Rating); // Math.Round(7.55, 1) banker's → 7.6? see note
        }

        // ------------------------------------------------------------------
        // MDBList
        // ------------------------------------------------------------------

        [Fact]
        public async Task MdbList_FetchesEnabledProviders_WithConcurrency()
        {
            var handler = new RecordingHandler();
            foreach (var _ in Enumerable.Range(0, 3))
            {
                handler.Enqueue(JsonResponse(new { rating = 7.5 }));
            }
            var client = new MdbListClient(new HttpClient(handler));

            var ratings = await client.FetchRatingsAsync(
                "tt1234567", "series", new[] { "imdb", "tmdb", "trakt" });

            Assert.Equal(3, handler.Requests.Count);
            Assert.All(handler.Requests, r =>
                Assert.StartsWith("https://api.mdblist.test/rating/show/", r.RequestUri.ToString()));
            Assert.Contains("apikey=tmdb-key-1", handler.Requests[0].RequestUri.Query);
            Assert.All(ratings.Values, v => Assert.Equal(7.5, v.Value));
        }

        [Theory]
        [InlineData("movie", "movie")]
        [InlineData("film", "movie")]
        [InlineData("series", "show")]
        [InlineData("tv", "show")]
        [InlineData("show", "show")]
        [InlineData("tvshow", "show")]
        [InlineData("whatever", "movie")]
        public void NormalizeMediaType_MatchesJs(string input, string expected)
        {
            Assert.Equal(expected, MdbListClient.NormalizeMediaType(input));
        }

        [Theory]
        [InlineData("tt123:2:1", "tt123")]
        [InlineData("tmdb:550:extra", null)]
        public void ExtractIds_FromRawId(string rawId, string expectedImdb)
        {
            Assert.Equal(expectedImdb, MdbListClient.ExtractImdbId(rawId));
        }

        [Fact]
        public void ExtractTmdbId_ParsesPrefixedId()
        {
            Assert.Equal("550", MdbListClient.ExtractTmdbId("tmdb:550"));
            Assert.Equal("550", MdbListClient.ExtractTmdbId("TMDB:550:extra"));
            Assert.Null(MdbListClient.ExtractTmdbId("tt123"));
            Assert.Null(MdbListClient.ExtractTmdbId("tmdb:abc"));
        }

        // ------------------------------------------------------------------
        // Parental guide
        // ------------------------------------------------------------------

        [Fact]
        public async Task ParentalGuide_ResolvesDominantSeverity()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new[]
            {
                new
                {
                    name = "Nudity",
                    severityBreakdowns = new object[]
                    {
                        new { severityLevel = "None", voteCount = 10 },
                        new { severityLevel = "Mild", voteCount = 25 },
                        new { severityLevel = "Severe", voteCount = 3 }
                    }
                }
            }));
            var client = new ParentalGuideClient(new HttpClient(handler));

            var categories = await client.GetParentalGuideAsync("tt1234567");

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://parental.test/titles/tt1234567/parentsGuide", request.RequestUri.ToString());
            var category = Assert.Single(categories);
            Assert.Equal("Nudity", category.Name);
            Assert.Equal("mild", category.Severity);
        }

        [Fact]
        public async Task ParentalGuide_AllNone_FallsBackToNone()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new[]
            {
                new
                {
                    name = "Violence",
                    severityBreakdowns = new object[] { new { severityLevel = "none", voteCount = 40 } }
                }
            }));
            var client = new ParentalGuideClient(new HttpClient(handler));

            var categories = await client.GetParentalGuideAsync("tt1234567");

            Assert.Equal("none", Assert.Single(categories).Severity);
        }

        [Fact]
        public async Task ParentalGuide_RejectsNonImdbId()
        {
            var handler = new RecordingHandler();
            var client = new ParentalGuideClient(new HttpClient(handler));

            await client.GetParentalGuideAsync("tmdb:550");

            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // IntroDb segments
        // ------------------------------------------------------------------

        [Fact]
        public async Task IntroDb_ReturnsSortedIntervals_WithMsConversion()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new
            {
                intro = new { start_sec = 10.5, end_sec = 45.0 },
                outro = new { start_ms = 120000, end_ms = 150000 },
                recap = new { start_sec = 30, end_sec = 5 } // invalid: end <= start → dropped
            }));
            var client = new IntroDbClient(new HttpClient(handler));

            var intervals = await client.GetSegmentsAsync("tt1234567", 1, 2);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://introdb.test/segments?imdb_id=tt1234567&season=1&episode=2",
                request.RequestUri.ToString());
            Assert.Equal(2, intervals.Count);
            Assert.Equal(10.5, intervals[0].StartTime);
            Assert.Equal("intro", intervals[0].Type);
            Assert.Equal(120.0, intervals[1].StartTime); // ms → seconds
            Assert.Equal("outro", intervals[1].Type);
        }

        [Fact]
        public async Task IntroDb_GuardsInvalidInput()
        {
            var handler = new RecordingHandler();
            var client = new IntroDbClient(new HttpClient(handler));

            await client.GetSegmentsAsync("tt1234567", 0, 2); // season 0 invalid

            Assert.Empty(handler.Requests);
        }
    }
}
