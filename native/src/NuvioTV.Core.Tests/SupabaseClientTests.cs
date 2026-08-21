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
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Tests;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for SupabaseClient against the JS behavioral spec:
    /// js/data/remote/supabase/supabaseApi.js and js/core/auth/supabaseAuthFetch.js.
    /// </summary>
    [Collection("StaticConfig")]
    public class SupabaseClientTests : IDisposable
    {
        private RecordingHandler _handler;
        private HttpClient _httpClient;

        public SupabaseClientTests()
        {
            AppConfig.ResetForTests();
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", null);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            AppConfig.ResetForTests();
        }

        private static void ConfigureSupabase(string url, string anonKey, string fallbackUrl)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["supabaseUrl"] = url,
                ["supabaseAnonKey"] = anonKey,
                ["supabaseFallbackUrl"] = fallbackUrl ?? ""
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        private SupabaseClient CreateClient(string accessToken = "session-token", string refreshToken = "refresh-token")
        {
            _handler = new RecordingHandler();
            _httpClient = new HttpClient(_handler);
            return new SupabaseClient(_httpClient, new StubSessionTokenProvider(accessToken, refreshToken));
        }

        private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // REST surface
        // ------------------------------------------------------------------

        [Fact]
        public async Task RpcAsync_PostsToRpcPath_WithApikeyAndSessionBearer()
        {
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("get_profile", new { user_id = "u1" });

            var request = Assert.Single(_handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://primary.supabase.co/rest/v1/rpc/get_profile", request.RequestUri.ToString());
            Assert.Equal("anon-test-key", request.Headers.GetValues("apikey").Single());
            Assert.Equal("Bearer session-token", request.Headers.Authorization.ToString());

            var body = await request.Content.ReadAsStringAsync();
            Assert.Equal("{\"user_id\":\"u1\"}", body);
        }

        [Fact]
        public async Task RpcAsync_UseSessionFalse_OmitsAuthorization()
        {
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("fn", new { }, useSession: false);

            var request = Assert.Single(_handler.Requests);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("anon-test-key", request.Headers.GetValues("apikey").Single());
        }

        [Fact]
        public async Task TableAsync_GetWithRawQuery_DeserializesRows()
        {
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new[] { new { id = "a" }, new { id = "b" } }));

            var rows = await client.TableAsync<JsonElement>("profiles", "id=in.(a,b)&order=name.asc");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://primary.supabase.co/rest/v1/profiles?id=in.(a,b)&order=name.asc", request.RequestUri.ToString());
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        public async Task UpsertAsync_SendsPreferHeader_AndOnConflictQuery()
        {
            var client = CreateClient();
            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

            await client.UpsertAsync("watchlist", new[] { new { id = "x" } }, onConflict: "user_id,item_id");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://primary.supabase.co/rest/v1/watchlist?on_conflict=user_id%2Citem_id", request.RequestUri.ToString());
            Assert.Equal("resolution=merge-duplicates,return=representation", Assert.Single(request.Headers.GetValues("Prefer")));
        }

        // ------------------------------------------------------------------
        // Auth endpoints
        // ------------------------------------------------------------------

        [Fact]
        public async Task SignupAnonymous_SendsTvClientBody_WithAnonKeyBearer()
        {
            var client = CreateClient(accessToken: "", refreshToken: "");
            _handler.Enqueue(JsonResponse(new { access_token = "new" }));

            await client.SignupAnonymousAsync();

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/signup", request.RequestUri.ToString());
            Assert.Equal("Bearer anon-test-key", request.Headers.Authorization.ToString());
            var body = await request.Content.ReadAsStringAsync();
            Assert.Equal("{\"data\":{\"tv_client\":\"webos\"}}", body);
        }

        [Fact]
        public async Task PasswordToken_GrantTypePassword_WithEmailBody()
        {
            var client = CreateClient(accessToken: "", refreshToken: "");
            _handler.Enqueue(JsonResponse(new { access_token = "t" }));

            await client.PasswordTokenAsync("a@b.c", "secret");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/token?grant_type=password", request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("a@b.c", doc.RootElement.GetProperty("email").GetString());
            Assert.Equal("secret", doc.RootElement.GetProperty("password").GetString());
        }

        [Fact]
        public async Task RefreshToken_GrantTypeRefreshToken_WithRefreshTokenBody()
        {
            var client = CreateClient(accessToken: "", refreshToken: "");
            _handler.Enqueue(JsonResponse(new { access_token = "t" }));

            await client.RefreshTokenAsync("rt-123");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/token?grant_type=refresh_token", request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("rt-123", doc.RootElement.GetProperty("refresh_token").GetString());
        }

        [Fact]
        public async Task AnonymousToken_EmptyJsonObjectBody()
        {
            var client = CreateClient(accessToken: "", refreshToken: "");
            _handler.Enqueue(JsonResponse(new { access_token = "t" }));

            await client.AnonymousTokenAsync();

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/token?grant_type=anonymous", request.RequestUri.ToString());
            Assert.Equal("{}", await request.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task TvLoginsExchange_ValidSessionJwt_UsesSessionBearer()
        {
            var jwt = JwtGenerator.GenerateToken(expiresInSeconds: 3600);
            var client = CreateClient(accessToken: jwt, refreshToken: "r");
            _handler.Enqueue(JsonResponse(new { session = true }));

            await client.TvLoginsExchangeAsync("CODE", "nonce-1");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("https://primary.supabase.co/functions/v1/tv-logins-exchange", request.RequestUri.ToString());
            Assert.Equal("Bearer " + jwt, request.Headers.Authorization.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("CODE", doc.RootElement.GetProperty("code").GetString());
            Assert.Equal("nonce-1", doc.RootElement.GetProperty("device_nonce").GetString());
        }

        [Fact]
        public async Task TvLoginsExchange_NoSession_FallsBackToAnonKey()
        {
            var client = CreateClient(accessToken: "", refreshToken: "");
            _handler.Enqueue(JsonResponse(new { session = true }));

            await client.TvLoginsExchangeAsync("CODE", "n");

            var request = Assert.Single(_handler.Requests);
            Assert.Equal("Bearer anon-test-key", request.Headers.Authorization.ToString());
        }

        // ------------------------------------------------------------------
        // Failover transport (supabaseAuthFetch.js)
        // ------------------------------------------------------------------

        [Fact]
        public async Task Failover_RetryableStatusOnPrimary_ReplaysAgainstFallback()
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://fallback.supabase.co");
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new { error = "boom" }, HttpStatusCode.BadGateway));
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("fn", new { });

            Assert.Equal(2, _handler.Requests.Count);
            Assert.Equal("https://primary.supabase.co/rest/v1/rpc/fn", _handler.Requests[0].RequestUri.ToString());
            Assert.Equal("https://fallback.supabase.co/rest/v1/rpc/fn", _handler.Requests[1].RequestUri.ToString());
        }

        [Theory]
        [InlineData(408)]
        [InlineData(500)]
        [InlineData(502)]
        [InlineData(503)]
        [InlineData(504)]
        [InlineData(520)]
        [InlineData(526)]
        [InlineData(530)]
        public async Task Failover_RetryableStatusMatrix_TriggersFallback(int status)
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://fallback.supabase.co");
            var client = CreateClient();
            _handler.Enqueue(new HttpResponseMessage((HttpStatusCode)status));
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("fn", new { });

            Assert.Equal(2, _handler.Requests.Count);
        }

        [Fact]
        public async Task Failover_NonRetryableStatus_ReturnedAsIs()
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://fallback.supabase.co");
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new { message = "nope" }, HttpStatusCode.BadRequest));
            var ex = await Assert.ThrowsAsync<NuvioHttpException>(() => client.RpcAsync("fn", new { }));

            Assert.Equal(400, ex.Status);
            Assert.Single(_handler.Requests);
        }

        [Fact]
        public async Task Failover_CloudflareHtmlBody_TriggersFallback()
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://fallback.supabase.co");
            var client = CreateClient();
            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>cloudflare error page</body></html>", Encoding.UTF8, "text/html")
            });
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("fn", new { });

            Assert.Equal(2, _handler.Requests.Count);
        }

        [Fact]
        public async Task Failover_NetworkErrorOnPrimary_TriggersFallback()
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://fallback.supabase.co");
            var client = CreateClient();
            _handler.FailNextWith(new HttpRequestException("connection refused"));
            _handler.Enqueue(JsonResponse(new { ok = true }));

            await client.RpcAsync("fn", new { });

            Assert.Equal(2, _handler.Requests.Count);
        }

        [Fact]
        public async Task Failover_SamePrimaryAndFallback_SingleRequest()
        {
            ConfigureSupabase("https://primary.supabase.co", "anon-test-key", "https://primary.supabase.co/");
            var client = CreateClient();
            _handler.Enqueue(JsonResponse(new { error = "boom" }, HttpStatusCode.InternalServerError));

            await Assert.ThrowsAsync<NuvioHttpException>(() => client.RpcAsync("fn", new { }));

            Assert.Single(_handler.Requests);
        }

        // ------------------------------------------------------------------
        // Session pipeline
        // ------------------------------------------------------------------

        [Fact]
        public async Task RestCall_UnauthorizedWithRefreshToken_RetriesAfterRefresh()
        {
            var expiringJwt = JwtGenerator.GenerateToken(expiresInSeconds: 3600);
            var provider = new StubSessionTokenProvider(expiringJwt, "refresh-token");
            _handler = new RecordingHandler();
            _httpClient = new HttpClient(_handler);
            var client = new SupabaseClient(_httpClient, provider);

            _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            _handler.Enqueue(JsonResponse(new[] { new { id = "row" } }));

            await client.TableAsync<JsonElement>("items");

            Assert.True(provider.ForceRefreshCalled);
            Assert.Equal(2, _handler.Requests.Count);
            // Second attempt carries the rotated token from the stub provider.
            Assert.Equal("Bearer refreshed-token", _handler.Requests[1].Headers.Authorization.ToString());
        }

        [Fact]
        public async Task RestCall_ExpiringSession_PreRefreshesBeforeSend()
        {
            var expiringJwt = JwtGenerator.GenerateToken(expiresInSeconds: 10); // within 30s leeway
            var provider = new StubSessionTokenProvider(expiringJwt, "refresh-token");
            _handler = new RecordingHandler();
            _handler.Enqueue(JsonResponse(new[] { new { id = "row" } }));
            _httpClient = new HttpClient(_handler);
            var client = new SupabaseClient(_httpClient, provider);

            _handler.Enqueue(JsonResponse(new { id = "row" }));

            await client.TableAsync<JsonElement>("items");

            Assert.True(provider.TryRefreshCalled);
            Assert.False(provider.ForceRefreshCalled);
            Assert.Equal("Bearer refreshed-token", Assert.Single(_handler.Requests).Headers.Authorization.ToString());
        }
    }

    /// <summary>
    /// Records every request and serves queued responses; supports one-shot failure injection
    /// for network-error failover paths.
    /// </summary>
    public sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new Queue<HttpResponseMessage>();
        private readonly Queue<Exception> _pendingFailures = new Queue<Exception>();

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

        public void Enqueue(HttpResponseMessage response)
        {
            _responses.Enqueue(response);
        }

        /// <summary>Queues a failure thrown on the next SendAsync call(s), in order.</summary>
        public void FailNextWith(Exception failure)
        {
            _pendingFailures.Enqueue(failure);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (this)
            {
                Requests.Add(request);
                if (_pendingFailures.Count > 0)
                {
                    throw _pendingFailures.Dequeue();
                }

                Assert.True(_responses.Count > 0, "RecordingHandler ran out of queued responses");
                return Task.FromResult(_responses.Dequeue());
            }
        }
    }
}
