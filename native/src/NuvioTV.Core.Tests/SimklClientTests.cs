using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Integrations.Simkl;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Tests;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for SimklClient against the JS behavioral spec:
    /// js/data/repository/simklAuthService.js (rate limits, backoff),
    /// simklSyncService.js, simklScrobbleService.js.
    /// </summary>
    [Collection("StaticConfig")]
    public class SimklClientTests : IDisposable
    {
        public SimklClientTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["simklClientId"] = "test-client-id",
                ["simklApiUrl"] = "https://api.simkl.test",
                ["simklAppName"] = "nuvio-test",
                ["appVersion"] = "1.2.3"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            AppConfig.ResetForTests();
        }

        private static SimklClient CreateClient(RecordingHandler handler, ISimklClock clock = null)
        {
            return new SimklClient(new HttpClient(handler), clock);
        }

        private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // URL + header shape
        // ------------------------------------------------------------------

        [Fact]
        public async Task StartPin_UrlAndQueryShape_MatchWebapp()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { result = "OK", user_code = "AB12" }));
            var client = CreateClient(handler);

            await client.StartPinAsync();

            var request = Assert.Single(handler.Requests);
            // AppVersion derives from the assembly, not config — assert the parameter
            // family and order without pinning the version value.
            Assert.StartsWith("https://api.simkl.test/oauth/pin?client_id=test-client-id&app-name=nuvio-test&app-version=",
                request.RequestUri.AbsoluteUri);
            Assert.Equal("test-client-id", Assert.Single(request.Headers.GetValues("simkl-client-id")));
            Assert.Null(request.Headers.Authorization);
        }

        [Fact]
        public async Task PollPin_EscapesUserCode_AndSkipsRetry()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { result = "KO" }));
            var client = CreateClient(handler);

            await client.PollPinAsync("CD 34");
            var request = Assert.Single(handler.Requests);
            Assert.Contains("/oauth/pin/CD%2034", request.RequestUri.AbsoluteUri);
        }

        [Fact]
        public async Task AuthenticatedRequest_CarriesBearerToken()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { user = new { name = "joe" } }));
            var client = CreateClient(handler);

            await client.PostUserSettingsAsync("tok-1");

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer tok-1", request.Headers.Authorization.ToString());
            Assert.Equal("https://api.simkl.test/users/settings", request.RequestUri.GetLeftPart(System.UriPartial.Path));
        }

        [Fact]
        public async Task GetAllItems_ExtendedQueryFamily_MatchesWebappOrder()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { shows = new object[0] }));
            var client = CreateClient(handler);

            await client.GetAllItemsAsync("tok", "shows");

            var request = Assert.Single(handler.Requests);
            var query = request.RequestUri.Query;
            var pathAndQuery = request.RequestUri.PathAndQuery;
            Assert.StartsWith("/sync/all-items/shows?extended=full_anime_seasons&episode_watched_at=yes&episode_tvdb_id=yes&include_all_episodes=yes&language=en&client_id=",
                pathAndQuery);
        }

        [Fact]
        public async Task GetAllItems_InvalidMediaType_Throws()
        {
            var client = CreateClient(new RecordingHandler());
            await Assert.ThrowsAsync<ArgumentException>(() => client.GetAllItemsAsync("tok", "books"));
        }

        [Fact]
        public async Task ScrobbleStop_Status409_TreatedAsSuccess()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { error = "not_found" }, HttpStatusCode.Conflict));
            var client = CreateClient(handler);

            var payload = new SimklScrobblePayload { Progress = 90 };
            var result = await client.ScrobbleAsync("tok", "stop", payload);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.simkl.test/scrobble/stop", request.RequestUri.GetLeftPart(System.UriPartial.Path));
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(90, doc.RootElement.GetProperty("progress").GetDouble());
        }

        [Fact]
        public async Task ScrobbleStart_PostsPayloadWithMediaIds()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { result = "OK" }));
            var client = CreateClient(handler);

            var payload = new SimklScrobblePayload
            {
                Progress = 42.5,
                Show = new SimklScrobbleMedia
                {
                    Title = "Dark",
                    Year = 2017,
                    Ids = new Dictionary<string, object> { ["tmdb"] = 70523 }
                },
                Episode = new SimklScrobbleEpisode { Season = 1, Number = 2, Title = "Lies" }
            };
            await client.ScrobbleAsync("tok", "start", payload);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://api.simkl.test/scrobble/start", request.RequestUri.GetLeftPart(System.UriPartial.Path));
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(42.5, doc.RootElement.GetProperty("progress").GetDouble());
            Assert.Equal(70523, ((JsonElement)doc.RootElement.GetProperty("show").GetProperty("ids").GetProperty("tmdb")).GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("episode").GetProperty("season").GetInt32());
        }

        [Fact]
        public async Task MissingClientId_ThrowsBeforeAnyRequest()
        {
            AppConfig.ResetForTests(); // no simklClientId configured
            var handler = new RecordingHandler();
            var client = CreateClient(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartPinAsync());

            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task UnauthenticatedEndpoint_WithNoToken_Succeeds()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { result = "OK" }));
            var client = CreateClient(handler);

            await client.StartPinAsync();

            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task AuthenticatedEndpoint_WithoutToken_Throws()
        {
            var handler = new RecordingHandler();
            var client = CreateClient(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostUserSettingsAsync(null));

            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // Rate limiting (virtual clock)
        // ------------------------------------------------------------------

        [Fact]
        public async Task RateLimit_TwoGets_WaitAtLeast100MsApart()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { a = 1 }));
            handler.Enqueue(JsonResponse(new { b = 2 }));
            var client = CreateClient(handler, clock);

            await client.GetActivitiesAsync("tok");
            await client.GetActivitiesAsync("tok");

            // Second GET must have been delayed to >= first gate + 100ms.
            Assert.True(clock.DelayLog.Count >= 1, "expected at least one rate-limit delay");
            Assert.True(clock.DelayLog[^1] >= 100 || clock.NowMs >= 100,
                $"second GET not rate limited; delays=[{string.Join(",", clock.DelayLog)}] now={clock.NowMs}");
        }

        [Fact]
        public async Task RateLimit_WriteInterval_IsOneSecond()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { ok = true }));
            handler.Enqueue(JsonResponse(new { ok = true }));
            var client = CreateClient(handler, clock);

            await client.PostUserSettingsAsync("tok");
            await client.PostUserSettingsAsync("tok");

            Assert.Contains(clock.DelayLog, d => d >= 1000);
        }

        // ------------------------------------------------------------------
        // Retry / backoff classification
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(429)]
        [InlineData(500)]
        [InlineData(502)]
        [InlineData(503)]
        public async Task TransientStatus_RetriesUpToFiveAttempts(int status)
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            for (var i = 0; i < 4; i++)
            {
                handler.Enqueue(new HttpResponseMessage((HttpStatusCode)status));
            }
            handler.Enqueue(JsonResponse(new { recovered = true }));
            var client = CreateClient(handler, clock);

            var result = await client.GetActivitiesAsync("tok");

            Assert.Equal(5, handler.Requests.Count);
            Assert.Equal(4, clock.DelayLog.Count);
        }

        [Fact]
        public async Task TransientStatus_ExhaustedAttempts_ThrowsHttpError()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            for (var i = 0; i < 5; i++)
            {
                handler.Enqueue(JsonResponse(new { message = "still down" }, HttpStatusCode.BadGateway));
            }
            var client = CreateClient(handler, clock);

            var ex = await Assert.ThrowsAsync<NuvioHttpException>(() => client.GetActivitiesAsync("tok"));

            Assert.Equal(502, ex.Status);
            Assert.Equal("still down", ex.Detail);
            Assert.Equal(5, handler.Requests.Count);
        }

        [Fact]
        public async Task NonTransientStatus_DoesNotRetry()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { message = "bad input" }, HttpStatusCode.BadRequest));
            var client = CreateClient(handler, clock);

            var ex = await Assert.ThrowsAsync<NuvioHttpException>(() => client.GetActivitiesAsync("tok"));

            Assert.Single(handler.Requests);
            Assert.Empty(clock.DelayLog);
        }

        [Fact]
        public async Task SyncWriteRateLimitError_UsesThreeSecondBackoff()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { error = "rate_limit" }, HttpStatusCode.BadRequest));
            handler.Enqueue(JsonResponse(new { ok = true }));
            var client = CreateClient(handler, clock);

            // /sync/ POST with rate_limit body → 3s backoff, then success.
            var request = new SimklRequest("/sync/watchlist")
                .WithMethod("POST")
                .WithBody(new { items = new object[0] })
                .WithToken("tok");
            await client.SendAsync(request);

            Assert.Equal(2, handler.Requests.Count);
            Assert.Contains(3000L, clock.DelayLog);
        }

        [Fact]
        public async Task Unauthorized_Throws401Immediately()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var client = CreateClient(handler, clock);

            var ex = await Assert.ThrowsAsync<NuvioHttpException>(() => client.GetActivitiesAsync("expired"));

            Assert.Equal(401, ex.Status);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task NetworkFailure_BackoffSequenceIsExponential()
        {
            var clock = new ManualSimklClock();
            var handler = new RecordingHandler();
            for (var i = 0; i < 4; i++)
            {
                handler.FailNextWith(new HttpRequestException("connection reset"));
            }
            handler.Enqueue(JsonResponse(new { ok = true }));
            var client = CreateClient(handler, clock);

            await client.GetActivitiesAsync("tok");

            // Backoff: 1s, 2s, 4s, 8s between the five attempts.
            Assert.Equal(new[] { 1000L, 2000L, 4000L, 8000L }, clock.DelayLog);
        }

        // ------------------------------------------------------------------
        // DTO deserialization
        // ------------------------------------------------------------------

        [Fact]
        public async Task PinStartResult_DeserializesWireFields()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new
            {
                result = "OK",
                user_code = "XYZ9",
                verification_uri = "https://simkl.com/pin",
                expires_in = 900,
                interval = 5
            }));
            var client = CreateClient(handler);

            var pin = await client.StartPinAsync();

            Assert.Equal("OK", pin.Result);
            Assert.Equal("XYZ9", pin.UserCode);
            Assert.Equal("https://simkl.com/pin", pin.VerificationUri);
            Assert.Equal(900, pin.ExpiresIn);
            Assert.Equal(5, pin.Interval);
        }

        [Fact]
        public async Task PinPoll_AccessTokenMapped()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { result = "OK", access_token = "at-9", device_code = "dc" }));
            var client = CreateClient(handler);

            var poll = await client.PollPinAsync("XYZ9");

            Assert.Equal("at-9", poll.AccessToken);
            Assert.Equal("dc", poll.DeviceCode);
        }
    }

    /// <summary>Virtual clock: records delays and advances instantly.</summary>
    public sealed class ManualSimklClock : ISimklClock
    {
        private long _nowMs;

        public List<long> DelayLog { get; } = new List<long>();

        public long NowMs => _nowMs;

        public long UtcNowMs() => _nowMs;

        public Task DelayAsync(long milliseconds, CancellationToken ct)
        {
            DelayLog.Add(milliseconds);
            _nowMs += milliseconds;
            return Task.CompletedTask;
        }
    }
}
