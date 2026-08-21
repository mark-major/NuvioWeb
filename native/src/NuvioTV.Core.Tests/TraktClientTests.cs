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
using NuvioTV.Core.Integrations.Trakt;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for TraktClient against js/data/repository/traktAuthService.js:
    /// fixed headers, device flow status mapping, refresh grant, pagination walk.
    /// </summary>
    [Collection("StaticConfig")]
    public class TraktClientTests : IDisposable
    {
        private readonly MemoryTraktStore _store = new MemoryTraktStore();

        public TraktClientTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["traktClientId"] = "cid-123",
                ["traktClientSecret"] = "secret-456",
                ["traktApiUrl"] = "https://api.trakt.test"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            AppConfig.ResetForTests();
        }

        private TraktClient CreateClient(RecordingHandler handler)
        {
            return new TraktClient(new HttpClient(handler), _store);
        }

        private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // Headers + device code start
        // ------------------------------------------------------------------

        [Fact]
        public async Task StartDeviceAuth_PostsClientId_WithTraktHeaders()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new
            {
                device_code = "dc",
                user_code = "5055CC66",
                verification_url = "https://trakt.tv/activate",
                expires_in = 600,
                interval = 5
            }));
            var client = CreateClient(handler);

            var code = await client.StartDeviceAuthAsync();

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.trakt.test/oauth/device/code", request.RequestUri.ToString());
            Assert.Equal("2", Assert.Single(request.Headers.GetValues("trakt-api-version")));
            Assert.Equal("cid-123", Assert.Single(request.Headers.GetValues("trakt-api-key")));
            Assert.Equal("application/json", request.Content.Headers.ContentType.MediaType);

            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("cid-123", doc.RootElement.GetProperty("client_id").GetString());

            Assert.Equal("5055CC66", code.UserCode);
            Assert.Equal("dc", _store.GetDeviceFlow().DeviceCode);
        }

        [Fact]
        public async Task StartDeviceAuth_RateLimitedWithSmallRetryAfter_RetriesOnce()
        {
            var handler = new RecordingHandler();
            var rateLimited = new HttpResponseMessage((HttpStatusCode)429);
            rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            handler.Enqueue(rateLimited);
            handler.Enqueue(JsonResponse(new { device_code = "dc2", user_code = "U2" }));
            var client = CreateClient(handler);

            var code = await client.StartDeviceAuthAsync();

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("dc2", code.DeviceCode);
        }

        [Fact]
        public async Task StartDeviceAuth_LargeRetryAfter_ThrowsRateLimitMessage()
        {
            var handler = new RecordingHandler();
            var rateLimited = new HttpResponseMessage((HttpStatusCode)429);
            rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            handler.Enqueue(rateLimited);
            var client = CreateClient(handler);

            var ex = await Assert.ThrowsAsync<TraktException>(() => client.StartDeviceAuthAsync());

            Assert.Contains("~5 min", ex.Message);
        }

        // ------------------------------------------------------------------
        // Poll status mapping (traktAuthService.js:158-223)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(400, TraktPollResultType.Pending)]
        [InlineData(409, TraktPollResultType.AlreadyUsed)]
        [InlineData(410, TraktPollResultType.Expired)]
        [InlineData(418, TraktPollResultType.Denied)]
        public async Task Poll_MapsStatuses(int status, TraktPollResultType expected)
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage((HttpStatusCode)status));
            _store.SaveDeviceFlow(new TraktDeviceCode { DeviceCode = "dc", Interval = 5 });
            var client = CreateClient(handler);

            var result = await client.PollDeviceTokenAsync();

            Assert.Equal(expected, result.Type);
        }

        [Fact]
        public async Task Poll_DeniedAndExpired_ClearsDeviceFlow()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage((HttpStatusCode)418));
            _store.SaveDeviceFlow(new TraktDeviceCode { DeviceCode = "dc" });
            var client = CreateClient(handler);

            await client.PollDeviceTokenAsync();

            Assert.Null(_store.GetDeviceFlow());
        }

        [Fact]
        public async Task Poll_SlowDown_GrowsIntervalByFive_Clamped()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage((HttpStatusCode)429));
            _store.SaveDeviceFlow(new TraktDeviceCode { DeviceCode = "dc" });
            _store.PollIntervalSeconds = 5;
            var client = CreateClient(handler);

            var result = await client.PollDeviceTokenAsync();

            Assert.Equal(TraktPollResultType.SlowDown, result.Type);
            Assert.Equal(10, result.PollIntervalSeconds);
            Assert.Equal(10, _store.PollIntervalSeconds);
        }

        [Fact]
        public async Task Poll_Approved_SavesToken()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new
            {
                access_token = "at",
                refresh_token = "rt",
                expires_in = 7776000,
                created_at = 1700000000
            }));
            _store.SaveDeviceFlow(new TraktDeviceCode { DeviceCode = "dc" });
            var client = CreateClient(handler);

            var result = await client.PollDeviceTokenAsync();

            Assert.Equal(TraktPollResultType.Approved, result.Type);
            Assert.Equal("at", _store.GetToken().AccessToken);
        }

        [Fact]
        public async Task Poll_NoDeviceCode_Fails()
        {
            var handler = new RecordingHandler();
            var client = CreateClient(handler);

            var result = await client.PollDeviceTokenAsync();

            Assert.Equal(TraktPollResultType.Failed, result.Type);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // Refresh grant (traktAuthService.js:226-268)
        // ------------------------------------------------------------------

        [Fact]
        public async Task Refresh_ValidTokenNotExpiring_NoRequest()
        {
            var handler = new RecordingHandler();
            _store.SaveToken(new TraktTokenResponse
            {
                AccessToken = "at",
                RefreshToken = "rt",
                ExpiresIn = 36000,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60
            });
            var client = CreateClient(handler);

            var refreshed = await client.RefreshTokenIfNeededAsync();

            Assert.True(refreshed);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Refresh_ExpiringToken_PostsRefreshGrant()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new
            {
                access_token = "new-at",
                refresh_token = "new-rt",
                expires_in = 7776000,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
            _store.SaveToken(new TraktTokenResponse
            {
                AccessToken = "old",
                RefreshToken = "old-rt",
                ExpiresIn = 3600,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3570 // 30s left, inside 60s leeway
            });
            var client = CreateClient(handler);
            var refreshed = await client.RefreshTokenIfNeededAsync();

            Assert.True(refreshed);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.trakt.test/oauth/token", request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("refresh_token", doc.RootElement.GetProperty("grant_type").GetString());
            Assert.Equal("old-rt", doc.RootElement.GetProperty("refresh_token").GetString());
            Assert.Equal("urn:ietf:wg:oauth:2.0:oob", doc.RootElement.GetProperty("redirect_uri").GetString());
            Assert.Equal("new-at", _store.GetToken().AccessToken);
        }

        [Fact]
        public async Task Refresh_Unauthorized_ClearsAuth()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            _store.SaveToken(new TraktTokenResponse
            {
                AccessToken = "dead",
                RefreshToken = "dead-rt",
                ExpiresIn = 3600,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200
            });
            var client = CreateClient(handler);

            var refreshed = await client.RefreshTokenIfNeededAsync();

            Assert.False(refreshed);
            Assert.Null(_store.GetToken());
        }

        // ------------------------------------------------------------------
        // Paginated sync reads
        // ------------------------------------------------------------------

        [Fact]
        public async Task FetchHistory_WalksPages_UntilShortPage()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(MakeHistoryPage(100)));
            handler.Enqueue(JsonResponse(MakeHistoryPage(30)));
            _store.SaveToken(ValidToken());
            var client = CreateClient(handler);

            var items = await client.FetchHistoryAsync(limit: 250);

            Assert.Equal(130, items.Count);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("https://api.trakt.test/sync/history?limit=100&page=1", handler.Requests[0].RequestUri.ToString());
            Assert.Equal("https://api.trakt.test/sync/history?limit=100&page=2", handler.Requests[1].RequestUri.ToString());
            Assert.Equal("Bearer at", handler.Requests[0].Headers.Authorization.ToString());
        }

        [Fact]
        public async Task FetchWatchlist_RespectsLimitCap()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(MakeHistoryPage(100)));
            handler.Enqueue(JsonResponse(MakeHistoryPage(100)));
            _store.SaveToken(ValidToken());
            var client = CreateClient(handler);

            var items = await client.FetchWatchlistAsync(limit: 150);

            Assert.Equal(150, items.Count);
            Assert.Equal("/sync/watchlist?limit=100&page=1", handler.Requests[0].RequestUri.PathAndQuery);
        }

        [Fact]
        public async Task FetchWatchedMovies_UsesUserIdPath()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new object[0]));
            _store.SaveToken(ValidToken());
            var client = CreateClient(handler);

            await client.FetchWatchedMoviesAsync();

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.trakt.test/users/me/watched/movies?extended=noseasons",
                request.RequestUri.ToString());
        }

        [Fact]
        public async Task Scrobble_Stop_PostsPayloadWithBearer()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { progress = 95.0 }));
            _store.SaveToken(ValidToken());
            var client = CreateClient(handler);

            var response = await client.ScrobbleAsync("stop", new { movie = new { title = "X" }, progress = 95.0 });

            Assert.True(response.IsSuccess);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.trakt.test/scrobble/stop", request.RequestUri.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer at", request.Headers.Authorization.ToString());
        }

        [Fact]
        public async Task NoTokens_SyncReadsReturnEmpty_WithoutRequests()
        {
            var handler = new RecordingHandler();
            var client = CreateClient(handler);

            var history = await client.FetchHistoryAsync();

            Assert.Empty(history);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static TraktTokenResponse ValidToken()
        {
            return new TraktTokenResponse
            {
                AccessToken = "at",
                RefreshToken = "rt",
                ExpiresIn = 360000,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60
            };
        }

        private static JsonElement MakeHistoryPage(int count)
        {
            var items = new List<object>();
            for (var i = 0; i < count; i++)
            {
                items.Add(new
                {
                    watched_at = "2024-01-01T00:00:00.000Z",
                    movie = new { title = "Movie " + i, year = 2020, ids = new { tmdb = 1000 + i } }
                });
            }
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(items));
        }

        private sealed class MemoryTraktStore : ITraktAuthStateStore
        {
            private TraktTokenResponse _token;
            private TraktDeviceCode _flow;

            public int PollIntervalSeconds { get; set; } = 5;

            public TraktTokenResponse GetToken() => _token;

            public void SaveToken(TraktTokenResponse token) => _token = token;

            public void ClearAuth() => _token = null;

            public void SaveDeviceFlow(TraktDeviceCode flow) => _flow = flow;

            public TraktDeviceCode GetDeviceFlow() => _flow;

            public void ClearDeviceFlow() => _flow = null;
        }
    }
}
