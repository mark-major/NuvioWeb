using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Networking;
using Xunit;

namespace NuvioTV.Core.Tests
{
    public class NetworkingTests
    {
        // Test (a): 401→refresh→success performs exactly 2 calls and Bearer token rotation
        [Fact]
        public async Task GetJsonAsync_401ThenRefreshThenSuccess_PerformsExactly2CallsWithNewToken()
        {
            // Arrange
            var callCount = 0;
            var authorizationHeaders = new List<string>();
            var handler = new StubHandler(request =>
            {
                var auth = request.Headers.Authorization?.ToString();
                authorizationHeaders.Add(auth ?? "null");
                callCount++;

                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"success\"}", Encoding.UTF8, "application/json")
                };
            });

            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("initial-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act
            var result = await client.GetJsonAsync<TestResponse>("http://test/api");

            // Assert
            Assert.Equal(2, callCount);
            Assert.True(tokenProvider.ForceRefreshCalled);
            Assert.NotNull(result);
            Assert.Equal("success", result.Data);
            
            // Assert Bearer headers: first call with initial token, second with refreshed token
            Assert.Equal(2, authorizationHeaders.Count);
            Assert.Equal("Bearer initial-token", authorizationHeaders[0]);
            Assert.Equal("Bearer refreshed-token", authorizationHeaders[1]);
        }

        // Test (b): expiring-token pre-refresh within 30s leeway
        [Fact]
        public async Task GetJsonAsync_ExpiringToken_PreRefreshesWithinLeeway()
        {
            // Arrange
            var authorizationHeaders = new List<string>();
            var handler = new StubHandler(request =>
            {
                var auth = request.Headers.Authorization?.ToString();
                authorizationHeaders.Add(auth ?? "null");
                
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"success\"}", Encoding.UTF8, "application/json")
                };
            });

            var httpClient = new HttpClient(handler);
            // Token expiring in 20 seconds (within 30s leeway)
            var tokenProvider = new StubSessionTokenProvider(
                JwtGenerator.GenerateToken(expiresInSeconds: 20),
                "valid-refresh"
            );
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act
            var result = await client.GetJsonAsync<TestResponse>("http://test/api");

            // Assert
            Assert.True(tokenProvider.TryRefreshCalled);
            Assert.NotNull(result);
            Assert.Equal("success", result.Data);
            
            // Should have used refreshed token
            Assert.Single(authorizationHeaders);
            Assert.Equal("Bearer refreshed-token", authorizationHeaders[0]);
        }

        // Test (c): 204 → null
        [Fact]
        public async Task GetJsonAsync_204NoContent_ReturnsNull()
        {
            // Arrange
            var handler = new StubHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });

            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("valid-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act
            var result = await client.GetJsonAsync<TestResponse>("http://test/api");

            // Assert
            Assert.Null(result);
        }

        // Test (d): concurrency=4 caps in-flight
        [Fact]
        public async Task MapWithConcurrency_Concurrency4_CapsInFlight()
        {
            // Arrange
            var delayMs = 50;
            
            Func<int, CancellationToken, Task<int>> mapper = async (item, ct) =>
            {
                await Task.Delay(delayMs, ct);
                return item * 2;
            };

            var items = Enumerable.Range(1, 20).ToList();

            // Act
            var (results, maxInFlight) = await MapWithConcurrency.RunAsyncTracked(4, items, mapper, CancellationToken.None);

            // Assert
            Assert.Equal(20, results.Count);
            for (var i = 0; i < results.Count; i++)
            {
                Assert.Equal(results[i], items[i] * 2);
            }
            
            // Max concurrency should be capped at 4 but should have achieved parallelism
            Assert.True(maxInFlight <= 4, $"Max in-flight {maxInFlight} should not exceed concurrency cap of 4");
            Assert.True(maxInFlight >= 2, $"Max in-flight {maxInFlight} should demonstrate parallelism (at least 2)");
        }

        // Test (e): error surfaces NuvioHttpException.Status
        [Fact]
        public async Task GetJsonAsync_HttpError_ThrowsNuvioHttpExceptionWithStatus()
        {
            // Arrange
            var handler = new StubHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"code\":\"NOT_FOUND\",\"detail\":\"Resource not found\"}", Encoding.UTF8, "application/json")
                };
            });

            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("valid-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<NuvioHttpException>(() =>
                client.GetJsonAsync<TestResponse>("http://test/api")
            );

            Assert.Equal(404, exception.Status);
            Assert.Equal("NOT_FOUND", exception.Code);
            Assert.Equal("Resource not found", exception.Detail);
        }

        [Fact]
        public async Task GetJsonAsync_HttpError_MessageKeyMapsToDetail()
        {
            var handler = new StubHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    // Supabase/PostgREST error bodies carry "message" (JS maps parsed.message → error.detail).
                    Content = new StringContent("{\"code\":\"PGRST116\",\"message\":\"No rows found\"}", Encoding.UTF8, "application/json")
                };
            });
            var client = new NuvioHttpClient(new HttpClient(handler), new StubSessionTokenProvider("valid-token", "valid-refresh"));

            var exception = await Assert.ThrowsAsync<NuvioHttpException>(() =>
                client.GetJsonAsync<TestResponse>("http://test/api")
            );

            Assert.Equal("PGRST116", exception.Code);
            Assert.Equal("No rows found", exception.Detail);
        }

        [Fact]
        public async Task GetJsonAsync_HttpError_NonStringCodeIsSkipped()
        {
            var handler = new StubHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"code\": 42, \"message\": \"Human readable\"}", Encoding.UTF8, "application/json")
                };
            });
            var client = new NuvioHttpClient(new HttpClient(handler), new StubSessionTokenProvider("valid-token", "valid-refresh"));

            var exception = await Assert.ThrowsAsync<NuvioHttpException>(() =>
                client.GetJsonAsync<TestResponse>("http://test/api")
            );

            Assert.Null(exception.Code);
            Assert.Equal("Human readable", exception.Detail);
        }

        [Fact]
        public async Task GetJsonAsync_HttpError_NonJsonBodyKeepsRawTextAsMessage()
        {
            const string rawBody = "<html>Gateway timeout</html>";
            var handler = new StubHandler(request =>
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent(rawBody, Encoding.UTF8, "text/html")
                };
            });
            var client = new NuvioHttpClient(new HttpClient(handler), new StubSessionTokenProvider("valid-token", "valid-refresh"));

            var exception = await Assert.ThrowsAsync<NuvioHttpException>(() =>
                client.GetJsonAsync<TestResponse>("http://test/api")
            );

            Assert.Equal(rawBody, exception.Message);
        }

        // Additional test: JWT payload decoding (exp claim extraction)
        [Fact]
        public void JwtDecoder_DecodeToken_ExtractsExpClaim()
        {
            // Arrange
            var token = JwtGenerator.GenerateToken(expiresInSeconds: 300);

            // Act
            var exp = JwtDecoder.GetExpirationSeconds(token);

            // Assert
            Assert.NotNull(exp);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.True(exp.Value > now); // Should be in the future
            Assert.True(exp.Value < now + 400); // Should be within expected range
        }

        [Fact]
        public void JwtDecoder_DecodeToken_WithBase64UrlCharacters_ExtractsExpClaim()
        {
            // Payload JSON chosen so its base64url encoding contains '-' and '_' (real-world JWTs do).
            var payloadJson = "{\"exp\":1893456000,\"typ\":\"JWT\",\"aud\":\"authenticated\"}";
            var token = JwtGenerator.GenerateRaw(payloadJson, expiresInSeconds: null);

            var exp = JwtDecoder.GetExpirationSeconds(token);

            Assert.NotNull(exp);
            Assert.Equal(1893456000L, exp.Value);
        }

        [Fact]
        public void JwtDecoder_DecodeToken_NonJwt_ReturnsNull()
        {
            Assert.Null(JwtDecoder.GetExpirationSeconds("not-a-jwt-token"));
        }

        [Fact]
        public void JwtDecoder_DecodeToken_InvalidExp_ReturnsNull()
        {
            var token = JwtGenerator.GenerateRaw("{\"exp\":\"soon\"}", expiresInSeconds: null);
            Assert.Null(JwtDecoder.GetExpirationSeconds(token));
        }

        // Test (f): includeSessionAuth=false omits Bearer header
        [Fact]
        public async Task GetJsonAsync_IncludeSessionAuthFalse_OmitsBearerHeader()
        {
            // Arrange
            var authorizationHeaders = new List<string>();
            var handler = new StubHandler(request =>
            {
                var auth = request.Headers.Authorization?.ToString();
                authorizationHeaders.Add(auth ?? "null");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"success\"}", Encoding.UTF8, "application/json")
                };
            });
            
            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("valid-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act
            var result = await client.GetJsonAsync<TestResponse>("http://test/api", includeSessionAuth: false);

            // Assert
            Assert.Single(authorizationHeaders);
            Assert.Equal("null", authorizationHeaders[0]);
            Assert.NotNull(result);
            Assert.Equal("success", result.Data);
            Assert.False(tokenProvider.TryRefreshCalled); // Should not attempt refresh when auth disabled
        }

        // Test (g): PostJsonAsync with includeSessionAuth=false
        [Fact]
        public async Task PostJsonAsync_IncludeSessionAuthFalse_OmitsBearerHeader()
        {
            // Arrange
            var authorizationHeaders = new List<string>();
            var handler = new StubHandler(request =>
            {
                var auth = request.Headers.Authorization?.ToString();
                authorizationHeaders.Add(auth ?? "null");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"posted\"}", Encoding.UTF8, "application/json")
                };
            });
            
            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("valid-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act
            var result = await client.PostJsonAsync<TestResponse>("http://test/api", new { data = "test" }, includeSessionAuth: false);

            // Assert
            Assert.Single(authorizationHeaders);
            Assert.Equal("null", authorizationHeaders[0]);
            Assert.NotNull(result);
            Assert.Equal("posted", result.Data);
        }

        // Test (h): Default includeSessionAuth=true includes Bearer header
        [Fact]
        public async Task GetJsonAsync_Default_IncludeSessionAuthTrue_IncludesBearerHeader()
        {
            // Arrange
            var authorizationHeaders = new List<string>();
            var handler = new StubHandler(request =>
            {
                var auth = request.Headers.Authorization?.ToString();
                authorizationHeaders.Add(auth ?? "null");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":\"success\"}", Encoding.UTF8, "application/json")
                };
            });
            
            var httpClient = new HttpClient(handler);
            var tokenProvider = new StubSessionTokenProvider("my-token", "valid-refresh");
            var client = new NuvioHttpClient(httpClient, tokenProvider);

            // Act (using default includeSessionAuth)
            var result = await client.GetJsonAsync<TestResponse>("http://test/api");

            // Assert
            Assert.Single(authorizationHeaders);
            Assert.Equal("Bearer my-token", authorizationHeaders[0]);
            Assert.NotNull(result);
            Assert.Equal("success", result.Data);
        }
    }

    // Test helpers and stubs

    public class TestResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; }
    }

    public class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, 
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    public class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, 
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    public class StubSessionTokenProvider : ISessionTokenProvider
    {
        private string _accessToken;
        private readonly string _refreshToken;

        public bool ForceRefreshCalled { get; private set; }
        public bool TryRefreshCalled { get; private set; }

        public StubSessionTokenProvider(string accessToken, string refreshToken)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
        }

        public string GetAccessToken() => _accessToken;

        public string GetRefreshToken() => _refreshToken;

        public bool HasTokens() => !string.IsNullOrEmpty(_accessToken) && !string.IsNullOrEmpty(_refreshToken);

        public Task<bool> TryRefreshAsync(bool force = false)
        {
            if (force)
            {
                ForceRefreshCalled = true;
            }
            else
            {
                TryRefreshCalled = true;
            }
            
            // Simulate token rotation
            _accessToken = "refreshed-token";
            
            return Task.FromResult(true);
        }
    }

    // JWT test helper
    public static class JwtGenerator
    {
        public static string GenerateToken(int expiresInSeconds = 300)
        {
            var header = new { alg = "HS256", typ = "JWT" };
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new 
            { 
                exp = now + expiresInSeconds,
                iat = now
            };

            var headerJson = JsonSerializer.Serialize(header);
            var payloadJson = JsonSerializer.Serialize(payload);

            var headerBase64 = Base64UrlEncode(headerJson);
            var payloadBase64 = Base64UrlEncode(payloadJson);
            
            // Note: In real scenario, signature would be computed with secret key
            // For testing, we just use a placeholder signature
            var signature = "test-signature";
            
            return $"{headerBase64}.{payloadBase64}.{signature}";
        }
        public static string GenerateRaw(string payloadJson, int? expiresInSeconds)
        {
            var header = new { alg = "HS256", typ = "JWT" };
            var headerBase64 = Base64UrlEncode(JsonSerializer.Serialize(header));
            var payloadBase64 = Base64UrlEncode(payloadJson);
            return $"{headerBase64}.{payloadBase64}.test-signature";
        }


        private static string Base64UrlEncode(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var base64 = Convert.ToBase64String(bytes);
            return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}