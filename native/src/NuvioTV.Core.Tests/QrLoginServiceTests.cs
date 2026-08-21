using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Storage;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for QrLoginService against js/core/auth/qrLoginService.js:
    /// start/poll/exchange RPC shapes, legacy-signature retry, token persistence.
    /// </summary>
    [Collection("StaticConfig")]
    public class QrLoginServiceTests : IDisposable
    {
        private readonly MemoryKeyValueStore _store = new MemoryKeyValueStore();

        public QrLoginServiceTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["supabaseUrl"] = "https://primary.supabase.co",
                ["supabaseAnonKey"] = "anon-key",
                ["tvLoginWebBaseUrl"] = "https://tv.nuvio.app"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            AppConfig.ResetForTests();
        }

        private (QrLoginService Service, RecordingHandler Handler, AuthManager Auth) Create()
        {
            var handler = new RecordingHandler();
            var auth = new AuthManager(new HttpClient(handler), _store);
            var delays = new List<TimeSpan>();
            var service = new QrLoginService(
                new HttpClient(handler),
                auth,
                _store,
                deviceLabelProvider: () => "Test TV",
                delay: (span, ct) =>
                {
                    delays.Add(span);
                    return Task.CompletedTask;
                });
            return (service, handler, auth);
        }
        private static void EnqueueAnonymousSignup(RecordingHandler handler)
        {
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = JwtGenerator.GenerateToken(3600), refresh_token = "rt-a" }),
                    Encoding.UTF8, "application/json")
            });
        }

        private static HttpResponseMessage RpcResponse(params object[] rows)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // Config guard
        // ------------------------------------------------------------------

        [Fact]
        public async Task Start_Unconfigured_ReturnsNullWithError()
        {
            AppConfig.ResetForTests(); // no supabase config
            var (service, handler, _) = Create();

            var result = await service.StartAsync();

            Assert.Null(result);
            Assert.Equal("QR auth is not configured", service.LastError);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // start_tv_login_session RPC
        // ------------------------------------------------------------------

        [Fact]
        public async Task StartTvLoginSession_PostsRpc_WithNonceRedirectAndDeviceName()
        {
            var (service, handler, _) = Create();
            EnqueueAnonymousSignup(handler);
            handler.Enqueue(RpcResponse(new
            {
                code = "ABCD-1234",
                qr_content = "https://tv.nuvio.app/login/ABCD-1234",
                poll_interval_seconds = 3,
                expires_at = "2026-01-01T00:05:00Z"
            }));

            var session = await service.StartTvLoginSessionAsync("nonce-1", "https://tv.nuvio.app");

            Assert.NotNull(session);
            var request = Assert.Single(handler.Requests, r =>
                r.RequestUri.ToString().Contains("/rest/v1/rpc/start_tv_login_session"));
            Assert.Equal("https://primary.supabase.co/rest/v1/rpc/start_tv_login_session",
                request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("nonce-1", doc.RootElement.GetProperty("p_device_nonce").GetString());
            Assert.Equal("https://tv.nuvio.app", doc.RootElement.GetProperty("p_redirect_base_url").GetString());
            Assert.Equal("Test TV", doc.RootElement.GetProperty("p_device_name").GetString());
        }

        [Fact]
        public async Task Start_FullFlow_BuildsResultWithFallbackQrUrl()
        {
            var (service, handler, auth) = Create();
            // Anonymous bootstrap: signup then anonymous token.
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = JwtGenerator.GenerateToken(3600), refresh_token = "rt-a" }),
                    Encoding.UTF8, "application/json")
            });
            handler.Enqueue(RpcResponse(new
            {
                code = "CODE-1",
                web_url = "https://tv.nuvio.app/login/CODE-1",
                poll_interval_seconds = 4
            }));

            var result = await service.StartAsync();

            Assert.NotNull(result);
            Assert.Equal("CODE-1", result.Code);
            Assert.Equal("https://tv.nuvio.app/login/CODE-1", result.LoginUrl);
            // No qr_image_url in payload → generated fallback URL carrying the login link.
            Assert.Contains("qrserver.com", result.QrImageUrl);
            Assert.Contains("login%2FCODE-1", Uri.EscapeDataString("https://tv.nuvio.app/login/CODE-1"));
            Assert.Equal(4, result.PollIntervalSeconds);
            Assert.Equal(result.DeviceNonce, service.CurrentDeviceNonce);
        }

        [Fact]
        public async Task Start_LegacySignatureError_RetriesWithoutDeviceName()
        {
            var (service, handler, auth) = Create();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = JwtGenerator.GenerateToken(3600), refresh_token = "rt-a" }),
                    Encoding.UTF8, "application/json")
            });
            // First attempt: legacy signature complaint.
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { message = "could not find the function start_tv_login_session (p_device_name)" }),
                    Encoding.UTF8, "application/json")
            });
            // Retry without p_device_name succeeds.
            handler.Enqueue(RpcResponse(new { code = "LEGACY-1", web_url = "https://x" }));

            var result = await service.StartAsync();

            Assert.NotNull(result);
            Assert.Equal(2, handler.Requests.Count(request =>
                request.RequestUri.ToString().Contains("start_tv_login_session")));
            var retry = handler.Requests.Last(request =>
                request.RequestUri.ToString().Contains("start_tv_login_session"));
            var body = await retry.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.TryGetProperty("p_device_name", out _));
        }

        // ------------------------------------------------------------------
        // poll RPC
        // ------------------------------------------------------------------

        [Fact]
        public async Task Poll_ReturnsStatusFromFirstRow()
        {
            var (service, handler, auth) = Create();
            EnqueueAnonymousSignup(handler);
            handler.Enqueue(RpcResponse(new { status = "pending" }));

            var status = await service.PollTvLoginSessionAsync("CODE-1", "nonce-1");

            Assert.Equal("pending", status);
            var request = Assert.Single(handler.Requests, r =>
                r.RequestUri.ToString().Contains("/rest/v1/rpc/poll_tv_login_session"));
            Assert.Equal("https://primary.supabase.co/rest/v1/rpc/poll_tv_login_session",
                request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("CODE-1", doc.RootElement.GetProperty("p_code").GetString());
            Assert.Equal("nonce-1", doc.RootElement.GetProperty("p_device_nonce").GetString());
        }

        [Fact]
        public async Task Poll_EmptyRows_ReturnsNull()
        {
            var (service, handler, auth) = Create();
            handler.Enqueue(RpcResponse(Array.Empty<object>()));

            Assert.Null(await service.PollTvLoginSessionAsync("C", "n"));
        }

        // ------------------------------------------------------------------
        // exchange
        // ------------------------------------------------------------------

        [Fact]
        public async Task Exchange_PersistsSessionTokens_AndAuthenticates()
        {
            var (service, handler, auth) = Create();
            EnqueueAnonymousSignup(handler);
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        session = new
                        {
                            access_token = JwtGenerator.GenerateToken(3600),
                            refresh_token = "rt-x"
                        }
                    }),
                    Encoding.UTF8, "application/json")
            });

            var exchanged = await service.ExchangeAsync("CODE-1", "nonce-1");

            Assert.True(exchanged);
            Assert.Equal(AuthState.Authenticated, auth.State);
            Assert.False(auth.IsAnonymousSession);
            Assert.NotNull(await SessionStore.GetAccessTokenAsync(_store));
            Assert.Equal("rt-x", await SessionStore.GetRefreshTokenAsync(_store));
        }

        [Fact]
        public async Task Exchange_MissingTokens_ReturnsFalseWithError()
        {
            var (service, handler, auth) = Create();
            EnqueueAnonymousSignup(handler);
            handler.Enqueue(RpcResponse(new { nope = true }));

            var exchanged = await service.ExchangeAsync("CODE-1", "nonce-1");

            Assert.False(exchanged);
            Assert.Equal("QR exchange missing session tokens", service.LastError);
        }
    }
}
