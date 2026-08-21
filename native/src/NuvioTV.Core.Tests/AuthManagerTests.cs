using System;
using System.Collections.Concurrent;
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
    /// Golden tests for AuthManager against js/core/auth/authManager.js:
    /// bootstrap, email sign-in, single-flight refresh with failure kinds,
    /// sign-out, anonymous-session gating.
    /// </summary>
    [Collection("StaticConfig")]
    public class AuthManagerTests : IDisposable
    {
        private readonly MemoryKeyValueStore _store = new MemoryKeyValueStore();

        public AuthManagerTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["supabaseUrl"] = "https://primary.supabase.co",
                ["supabaseAnonKey"] = "anon-key"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            AppConfig.ResetForTests();
        }

        private AuthManager CreateAuth(RecordingHandler handler)
        {
            return new AuthManager(new HttpClient(handler), _store);
        }

        private static HttpResponseMessage TokenResponse(string access, string refresh)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh }),
                    Encoding.UTF8, "application/json")
            };
        }

        // ------------------------------------------------------------------
        // Bootstrap
        // ------------------------------------------------------------------

        [Fact]
        public async Task Initialize_EmptyStore_SignedOut_WithoutRequests()
        {
            var handler = new RecordingHandler();
            var auth = CreateAuth(handler);

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Initialize_ValidStoredSession_AuthenticatedWithoutNetwork()
        {
            var handler = new RecordingHandler();
            await SessionStore.SetAccessTokenAsync(_store, JwtGenerator.GenerateToken(expiresInSeconds: 3600));
            await SessionStore.SetRefreshTokenAsync(_store, "rt");
            var auth = CreateAuth(handler);

            await auth.InitializeAsync();

            Assert.Equal(AuthState.Authenticated, auth.State);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Initialize_AnonymousStoredSession_SignedOut()
        {
            var handler = new RecordingHandler();
            await SessionStore.SetAccessTokenAsync(_store, JwtGenerator.GenerateToken(expiresInSeconds: 3600));
            await SessionStore.SetRefreshTokenAsync(_store, "rt");
            await SessionStore.SetIsAnonymousSessionAsync(_store, true);
            var auth = CreateAuth(handler);

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Initialize_ExpiringSession_RefreshesAndPersistsRotation()
        {
            var handler = new RecordingHandler();
            // 30s left — inside the 60s leeway window.
            await SessionStore.SetAccessTokenAsync(_store, JwtGenerator.GenerateToken(expiresInSeconds: 30));
            await SessionStore.SetRefreshTokenAsync(_store, "old-rt");
            handler.Enqueue(TokenResponse("new-at", "new-rt"));
            var auth = CreateAuth(handler);

            await auth.InitializeAsync();

            Assert.Equal(AuthState.Authenticated, auth.State);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/token?grant_type=refresh_token",
                request.RequestUri.ToString());
            Assert.Equal("new-at", await SessionStore.GetAccessTokenAsync(_store));
            Assert.Equal("new-rt", await SessionStore.GetRefreshTokenAsync(_store));
        }

        // ------------------------------------------------------------------
        // Email sign-in
        // ------------------------------------------------------------------

        [Fact]
        public async Task SignInWithEmail_PostsPasswordGrant_PersistsTokens()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(TokenResponse("at-1", "rt-1"));
            var auth = CreateAuth(handler);

            await auth.SignInWithEmailAsync("a@b.c", "pw");

            Assert.Equal(AuthState.Authenticated, auth.State);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://primary.supabase.co/auth/v1/token?grant_type=password",
                request.RequestUri.ToString());
            var body = await request.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("a@b.c", doc.RootElement.GetProperty("email").GetString());
            Assert.Equal("at-1", auth.GetAccessToken());
            Assert.Equal("at-1", await SessionStore.GetAccessTokenAsync(_store));
            Assert.False(await SessionStore.GetIsAnonymousSessionAsync(_store));
        }

        // ------------------------------------------------------------------
        // Refresh semantics
        // ------------------------------------------------------------------

        [Fact]
        public async Task Refresh_RejectedByServer_FalseWithRejectedKind()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var auth = CreateAuth(handler);
            await auth.StoreTokensAsync(JwtGenerator.GenerateToken(expiresInSeconds: -10), "rt", false);

            var refreshed = await auth.RefreshSessionIfNeededAsync(true);

            Assert.False(refreshed);
            Assert.Equal("rejected", auth.LastRefreshFailureKind);
        }

        [Fact]
        public async Task Refresh_TransientNetworkError_KeepsSession()
        {
            var handler = new RecordingHandler();
            handler.FailNextWith(new HttpRequestException("connection reset"));
            var auth = CreateAuth(handler);
            var stillValid = JwtGenerator.GenerateToken(expiresInSeconds: -10); // expired but present
            await auth.StoreTokensAsync(stillValid, "rt", false);

            var refreshed = await auth.RefreshSessionIfNeededAsync(true);

            Assert.True(refreshed);
            Assert.Equal("transient", auth.LastRefreshFailureKind);
            // Old token kept for retry later.
            Assert.Equal(stillValid, auth.GetAccessToken());
        }

        [Fact]
        public async Task Refresh_NoRefreshToken_UnexpiredJwtSuffices()
        {
            var handler = new RecordingHandler();
            var auth = CreateAuth(handler);
            await auth.StoreTokensAsync(JwtGenerator.GenerateToken(expiresInSeconds: 3600), null, false);

            var refreshed = await auth.RefreshSessionIfNeededAsync(true);

            Assert.True(refreshed);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public void IsAccessTokenExpired_NullTokenCountsAsExpired()
        {
            var auth = CreateAuth(new RecordingHandler());
            Assert.True(auth.IsAccessTokenExpired()); // no token → not JWT-like → expired
        }

        // ------------------------------------------------------------------
        // Sign out
        // ------------------------------------------------------------------

        [Fact]
        public async Task SignOut_ClearsStore_AndNotifiesSignedOut()
        {
            var handler = new RecordingHandler();
            var states = new List<AuthState>();
            var auth = CreateAuth(handler);
            using (auth.Subscribe(states.Add))
            {
                await auth.StoreTokensAsync("at", "rt", false);
                await auth.SignOutAsync();
            }

            Assert.Equal(AuthState.SignedOut, auth.State);
            Assert.Null(await SessionStore.GetAccessTokenAsync(_store));
            Assert.Null(await SessionStore.GetRefreshTokenAsync(_store));
            Assert.Equal(AuthState.SignedOut, states[^1]);
        }
    }

    /// <summary>Thread-safe in-memory IKeyValueStore.</summary>
    public sealed class MemoryKeyValueStore : IKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values =
            new ConcurrentDictionary<string, string>();

        public Task<string> GetAsync(string key)
        {
            return Task.FromResult(_values.TryGetValue(key ?? "", out var value) ? value : null);
        }

        public Task SetAsync(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key is required", nameof(key));
            }
            if (value == null)
            {
                _values.TryRemove(key, out _);
            }
            else
            {
                _values[key] = value;
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            _values.TryRemove(key ?? "", out _);
            return Task.CompletedTask;
        }
    }
}
