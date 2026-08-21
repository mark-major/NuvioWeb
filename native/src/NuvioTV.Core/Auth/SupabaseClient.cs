using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Auth
{
    /// <summary>
    /// Supabase protocol client: PostgREST RPC/select/upsert plus Supabase auth endpoints.
    ///
    /// Behavioral spec (do not diverge):
    /// - js/data/remote/supabase/supabaseApi.js (rpc/select/upsert header + URL shape)
    /// - js/core/auth/supabaseAuthFetch.js (primary-to-fallback failover + retry rules)
    /// - js/core/auth/authManager.js (password/refresh token grants)
    /// - js/core/auth/qrLoginService.js (anonymous signup/token, tv-logins-exchange)
    /// </summary>
    public sealed class SupabaseClient
    {
        // JS parity (js/core/auth/supabaseAuthFetch.js:3-5).
        private static readonly HashSet<int> RetryableStatuses = new HashSet<int>
        {
            408, 500, 502, 503, 504, 520, 521, 522, 523, 524, 525, 526, 530
        };

        private const int JwtExpirationLeewaySeconds = 30;

        private readonly HttpClient _httpClient;
        private readonly ISessionTokenProvider _tokens;

        public SupabaseClient(HttpClient httpClient, ISessionTokenProvider tokens)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        // ---------------------------------------------------------------------
        // REST (js/data/remote/supabase/supabaseApi.js)
        // ---------------------------------------------------------------------

        /// <summary>
        /// JS parity: SupabaseApi.rpc (supabaseApi.js:16-23). POST /rest/v1/rpc/{fn}
        /// with apikey + session Bearer headers and a JSON payload.
        /// </summary>
        public Task<JsonElement> RpcAsync(string fn, object payload, CancellationToken ct)
        {
            return RpcAsync(fn, payload, true, ct);
        }

        public Task<JsonElement> RpcAsync(string fn, object payload, bool useSession = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fn))
            {
                throw new ArgumentException("Function name is required", nameof(fn));
            }

            var path = "rest/v1/rpc/" + Uri.EscapeDataString(fn.Trim());
            return SendRestAsync<JsonElement>(HttpMethod.Post, path, payload, null, useSession, ct);
        }

        /// <summary>
        /// JS parity: SupabaseApi.select (supabaseApi.js:25-32). GET /rest/v1/{table}?{query};
        /// <paramref name="query"/> is the raw PostgREST query string without the leading '?'.
        /// </summary>
        public Task<IReadOnlyList<T>> TableAsync<T>(string table, string query = null, bool useSession = true, CancellationToken ct = default)
        {
            return SendRestAsync<IReadOnlyList<T>>(HttpMethod.Get, BuildTablePath(table, query), null, null, useSession, ct);
        }

        /// <summary>
        /// JS parity: SupabaseApi.upsert (supabaseApi.js:34-48). POST /rest/v1/{table}
        /// with Prefer: resolution=merge-duplicates,return=representation and an optional
        /// on_conflict query parameter.
        /// </summary>
        public Task UpsertAsync(string table, object rows, string onConflict = null, bool useSession = true, CancellationToken ct = default)
        {
            var query = onConflict != null ? "on_conflict=" + Uri.EscapeDataString(onConflict) : null;
            return SendRestAsync<object>(
                HttpMethod.Post,
                BuildTablePath(table, query),
                rows,
                "resolution=merge-duplicates,return=representation",
                useSession,
                ct);
        }

        private static string BuildTablePath(string table, string query)
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                throw new ArgumentException("Table name is required", nameof(table));
            }

            var path = "rest/v1/" + table.Trim();
            return string.IsNullOrEmpty(query) ? path : path + "?" + query;
        }

        // ---------------------------------------------------------------------
        // Auth (js/core/auth/authManager.js, js/core/auth/qrLoginService.js)
        // ---------------------------------------------------------------------

        /// <summary>
        /// JS parity: tryAnonymousSignup (qrLoginService.js:256-269). POST /auth/v1/signup
        /// with anon-key Bearer and body {"data":{"tv_client":"webos"}}.
        /// </summary>
        public Task<JsonElement> SignupAnonymousAsync(CancellationToken ct = default)
        {
            var request = new AuthRequest("POST", "/auth/v1/signup", "{\"data\":{\"tv_client\":\"webos\"}}");
            request.Headers["Authorization"] = "Bearer " + RequireAnonKey();
            return SendAuthAsync(request, ct);
        }

        /// <summary>
        /// JS parity: signInWithEmail (authManager.js:122-130).
        /// POST /auth/v1/token?grant_type=password with {"email","password"}.
        /// </summary>
        public Task<JsonElement> PasswordTokenAsync(string email, string password, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new PasswordGrantBody
            {
                Email = email,
                Password = password
            });
            return SendAuthAsync(new AuthRequest("POST", "/auth/v1/token?grant_type=password", body), ct);
        }

        /// <summary>
        /// JS parity: refreshSessionIfNeeded (authManager.js:168-175).
        /// POST /auth/v1/token?grant_type=refresh_token with {"refresh_token"}.
        /// </summary>
        public Task<JsonElement> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new RefreshGrantBody
            {
                RefreshToken = refreshToken
            });
            return SendAuthAsync(new AuthRequest("POST", "/auth/v1/token?grant_type=refresh_token", body), ct);
        }

        /// <summary>
        /// JS parity: tryAnonymousToken (qrLoginService.js:271-282).
        /// POST /auth/v1/token?grant_type=anonymous with an empty JSON object.
        /// </summary>
        public Task<JsonElement> AnonymousTokenAsync(CancellationToken ct = default)
        {
            return SendAuthAsync(new AuthRequest("POST", "/auth/v1/token?grant_type=anonymous", "{}"), ct);
        }

        /// <summary>
        /// JS parity: QrLoginService.exchange (qrLoginService.js:466-479).
        /// POST /functions/v1/tv-logins-exchange with {"code","device_nonce"}; the Bearer
        /// token is the unexpired session JWT when available, else the anon key
        /// (getBearerToken, qrLoginService.js:46-52).
        /// </summary>
        public Task<JsonElement> TvLoginsExchangeAsync(string code, string deviceNonce, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new TvLoginExchangeBody
            {
                Code = code,
                DeviceNonce = deviceNonce
            });
            var request = new AuthRequest("POST", "/functions/v1/tv-logins-exchange", body);
            request.Headers["Authorization"] = "Bearer " + ResolveExchangeBearer();
            return SendAuthAsync(request, ct);
        }

        private string ResolveExchangeBearer()
        {
            var accessToken = _tokens.GetAccessToken();
            if (IsJwtLike(accessToken) && !IsJwtExpired(accessToken, 0))
            {
                return accessToken;
            }

            return RequireAnonKey();
        }

        // JS parity (qrLoginService.js:14-17): three dot-separated segments.
        private static bool IsJwtLike(string token)
        {
            var value = (token ?? "").Trim();
            return value.Split('.').Length == 3;
        }

        // JS parity (qrLoginService.js:33-44): missing or unparsable exp means NOT expired.
        private static bool IsJwtExpired(string token, int leewaySeconds)
        {
            if (!IsJwtLike(token))
            {
                return true;
            }

            var exp = JwtDecoder.GetExpirationSeconds(token);
            if (!exp.HasValue || exp.Value <= 0)
            {
                return false;
            }

            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return exp.Value <= nowSeconds + leewaySeconds;
        }

        private static string RequireAnonKey()
        {
            var key = AppConfig.SupabaseAnonKey;
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException("Supabase anon key is not configured");
            }

            return key;
        }

        // ---------------------------------------------------------------------
        // Transport
        // ---------------------------------------------------------------------

        // Auth endpoints never attach session state or trigger refreshes; they carry
        // exactly the headers the JS call sites pass to fetchSupabaseAuth.
        private async Task<JsonElement> SendAuthAsync(AuthRequest spec, CancellationToken ct)
        {
            var response = await SendWithFailoverAsync(baseUrl => BuildAuthRequest(spec, baseUrl), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response).ConfigureAwait(false);
            return await ReadJsonElementAsync(response).ConfigureAwait(false);
        }

        // Session-aware REST pipeline; mirrors NuvioHttpClient.SendJsonAsync
        // (pre-refresh of expiring JWTs, 401 forced-refresh retry) on top of the
        // Supabase failover transport.
        private async Task<T> SendRestAsync<T>(HttpMethod method, string path, object body, string preferHeader, bool useSession, CancellationToken ct)
        {
            if (useSession)
            {
                var accessToken = _tokens.GetAccessToken();
                if (!string.IsNullOrEmpty(accessToken) && IsTokenExpiringSoon(accessToken))
                {
                    await _tokens.TryRefreshAsync(false).ConfigureAwait(false);
                }
            }

            var response = await SendWithFailoverAsync(
                baseUrl => BuildRestRequest(method, baseUrl, path, body, preferHeader, useSession),
                ct).ConfigureAwait(false);

            if (useSession &&
                response.StatusCode == HttpStatusCode.Unauthorized &&
                _tokens.GetRefreshToken() != null)
            {
                var refreshed = await _tokens.TryRefreshAsync(true).ConfigureAwait(false);
                if (refreshed)
                {
                    response = await SendWithFailoverAsync(
                        baseUrl => BuildRestRequest(method, baseUrl, path, body, preferHeader, useSession),
                        ct).ConfigureAwait(false);
                }
            }

            await EnsureSuccessAsync(response).ConfigureAwait(false);
            return await DeserializeAsync<T>(response).ConfigureAwait(false);
        }

        /// <summary>
        /// JS parity: fetchSupabaseAuth (supabaseAuthFetch.js:47-66). Sends to the primary
        /// base URL first; when a fallback is configured and differs from the primary, a
        /// retryable status / Cloudflare-style HTML body or a network error replays the
        /// identical request against the fallback base URL. Any other outcome is returned
        /// (or thrown) as-is.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithFailoverAsync(Func<string, HttpRequestMessage> requestFactory, CancellationToken ct)
        {
            var primaryBaseUrl = NormalizeBaseUrl(AppConfig.SupabaseUrl);
            if (string.IsNullOrEmpty(primaryBaseUrl))
            {
                throw new InvalidOperationException("Supabase URL is not configured");
            }

            var fallbackBaseUrl = NormalizeBaseUrl(AppConfig.SupabaseFallbackUrl);
            var canFallback =
                !string.IsNullOrEmpty(fallbackBaseUrl) &&
                !string.Equals(fallbackBaseUrl, primaryBaseUrl, StringComparison.OrdinalIgnoreCase);

            try
            {
                var response = await _httpClient.SendAsync(requestFactory(primaryBaseUrl), ct).ConfigureAwait(false);
                if (!canFallback || !await IsRetryableResponseAsync(response).ConfigureAwait(false))
                {
                    return response;
                }
            }
            catch (Exception error) when (canFallback && IsNetworkError(error))
            {
                // JS parity (supabaseAuthFetch.js:59-63): swallow network errors and
                // retry against the fallback host.
            }

            return await _httpClient.SendAsync(requestFactory(fallbackBaseUrl), ct).ConfigureAwait(false);
        }

        // JS parity: isRetryableResponse (supabaseAuthFetch.js:32-45): retryable status
        // set, or a body mentioning cloudflare / cf-error-code (case-insensitive).
        private static async Task<bool> IsRetryableResponseAsync(HttpResponseMessage response)
        {
            if (response == null)
            {
                return false;
            }

            if (RetryableStatuses.Contains((int)response.StatusCode))
            {
                return true;
            }

            try
            {
                var body = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).ToLowerInvariant();
                return body.Contains("cloudflare") || body.Contains("cf-error-code");
            }
            catch
            {
                return false;
            }
        }

        // JS parity: isNetworkError (supabaseAuthFetch.js:19-30). .NET folds transport
        // failures into HttpRequestException chains, so the message scan walks the
        // inner-exception chain the way a browser surfaces one flattened TypeError.
        private static bool IsNetworkError(Exception error)
        {
            if (error == null)
            {
                return false;
            }

            var name = error.GetType().Name.ToLowerInvariant();
            if (name == "typeerror")
            {
                return true;
            }

            var message = FlattenMessage(error).ToLowerInvariant();
            return message.Contains("failed to fetch")
                || message.Contains("network")
                || message.Contains("load failed")
                || message.Contains("connection")
                || message.Contains("ssl");
        }

        private static string FlattenMessage(Exception error)
        {
            var parts = new List<string>();
            for (var current = error; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrEmpty(current.Message))
                {
                    parts.Add(current.Message);
                }
            }

            return string.Join(" ", parts);
        }

        // JS parity: normalizeBaseUrl (supabaseAuthFetch.js:7-11).
        private static string NormalizeBaseUrl(string value)
        {
            return (value ?? "").Trim().TrimEnd('/');
        }

        private HttpRequestMessage BuildRestRequest(HttpMethod method, string baseUrl, string path, object body, string preferHeader, bool useSession)
        {
            var request = new HttpRequestMessage(method, baseUrl + "/" + path);
            ApplyCommonHeaders(request, preferHeader);

            if (useSession)
            {
                var accessToken = _tokens.GetAccessToken();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
            }

            ApplyJsonBody(request, method, body);
            return request;
        }

        private HttpRequestMessage BuildAuthRequest(AuthRequest spec, string baseUrl)
        {
            var request = new HttpRequestMessage(new HttpMethod(spec.Method), baseUrl + spec.Path);
            ApplyCommonHeaders(request, null);

            foreach (var pair in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            if (spec.Body != null)
            {
                request.Content = new StringContent(spec.Body, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return request;
        }

        // JS parity (supabaseApi.js:4-13): apikey rides along on every Supabase request.
        private void ApplyCommonHeaders(HttpRequestMessage request, string preferHeader)
        {
            request.Headers.TryAddWithoutValidation("apikey", RequireAnonKey());
            if (preferHeader != null)
            {
                request.Headers.TryAddWithoutValidation("Prefer", preferHeader);
            }
        }

        private static void ApplyJsonBody(HttpRequestMessage request, HttpMethod method, object body)
        {
            if (body == null || method == HttpMethod.Get || method == HttpMethod.Head)
            {
                return;
            }

            // Exact JS wire format: Content-Type "application/json" without charset suffix.
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        // Error mapping mirrors NuvioHttpClient.ThrowNuvioHttpExceptionAsync
        // (JS parity: js/core/network/httpClient.js:78-86).
        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var status = (int)response.StatusCode;
            string code = null;
            string detail = null;
            string rawBody = null;

            try
            {
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.String)
                        {
                            code = codeElem.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("message", out var messageElem) && messageElem.ValueKind == JsonValueKind.String)
                        {
                            detail = messageElem.GetString();
                        }
                        else if (detail == null && doc.RootElement.TryGetProperty("detail", out var detailElem) && detailElem.ValueKind == JsonValueKind.String)
                        {
                            detail = detailElem.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        rawBody = content;
                    }
                }
            }
            catch
            {
                detail = response.ReasonPhrase;
            }

            throw new NuvioHttpException(status, code, detail, rawBody);
        }

        private static async Task<JsonElement> ReadJsonElementAsync(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default;
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return default;
            }

            return JsonSerializer.Deserialize<JsonElement>(content);
        }

        private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default;
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(content);
        }

        private bool IsTokenExpiringSoon(string token)
        {
            var expSeconds = JwtDecoder.GetExpirationSeconds(token);
            if (!expSeconds.HasValue)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (expSeconds.Value - now) <= JwtExpirationLeewaySeconds;
        }

        private sealed class AuthRequest
        {
            internal string Method { get; }
            internal string Path { get; }
            internal string Body { get; }
            internal IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            internal AuthRequest(string method, string path, string body)
            {
                Method = method;
                Path = path;
                Body = body;
            }
        }

        private sealed class PasswordGrantBody
        {
            [JsonPropertyName("email")]
            public string Email { get; set; }

            [JsonPropertyName("password")]
            public string Password { get; set; }
        }

        private sealed class RefreshGrantBody
        {
            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; }
        }

        private sealed class TvLoginExchangeBody
        {
            [JsonPropertyName("code")]
            public string Code { get; set; }

            [JsonPropertyName("device_nonce")]
            public string DeviceNonce { get; set; }
        }
    }
}
