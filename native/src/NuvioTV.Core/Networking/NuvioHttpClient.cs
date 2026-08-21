using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NuvioTV.Core.Networking
{
    public interface ISessionTokenProvider
    {
        string GetAccessToken();
        string GetRefreshToken();
        bool HasTokens();
        Task<bool> TryRefreshAsync(bool force = false);
    }

    public class NuvioHttpException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public string Detail { get; }

        public NuvioHttpException(int status, string code, string detail, string rawBody = null)
            : base(detail ?? rawBody ?? $"HTTP error: {status}")
        {
            Status = status;
            Code = code;
            Detail = detail;
        }
    }

    public sealed class NuvioHttpClient
    {
        private readonly HttpClient _httpClient;
        private readonly ISessionTokenProvider _tokenProvider;
        private const int JwtExpirationLeewaySeconds = 30;

        public NuvioHttpClient(HttpClient inner, ISessionTokenProvider tokens)
        {
            _httpClient = inner ?? throw new ArgumentNullException(nameof(inner));
            _tokenProvider = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        public async Task<T> GetJsonAsync<T>(string url, bool includeSessionAuth = true, CancellationToken ct = default)
        {
            return await SendJsonAsync<T>(HttpMethod.Get, url, null, includeSessionAuth, ct);
        }

        public async Task<T> PostJsonAsync<T>(string url, object body, bool includeSessionAuth = true, CancellationToken ct = default)
        {
            return await SendJsonAsync<T>(HttpMethod.Post, url, body, includeSessionAuth, ct);
        }

        private async Task<T> SendJsonAsync<T>(HttpMethod method, string url, object body, bool includeSessionAuth, CancellationToken ct)
        {
            // Pre-refresh expiring JWT
            if (includeSessionAuth)
            {
                var accessToken = _tokenProvider.GetAccessToken();
                if (!string.IsNullOrEmpty(accessToken) && IsTokenExpiringSoon(accessToken))
                {
                    await _tokenProvider.TryRefreshAsync(force: false);
                }
            }

            var response = await SendRequestAsync(method, url, body, includeSessionAuth, ct);

            // 401 retry with forced refresh
            if (response.StatusCode == HttpStatusCode.Unauthorized && includeSessionAuth && _tokenProvider.GetRefreshToken() != null)
            {
                var refreshed = await _tokenProvider.TryRefreshAsync(force: true);
                if (refreshed)
                {
                    response = await SendRequestAsync(method, url, body, includeSessionAuth, ct);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                await ThrowNuvioHttpExceptionAsync(response);
            }

            // 204 No Content → default(T)/null
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return default(T);
            }

            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(content))
            {
                return default(T);
            }

            return JsonSerializer.Deserialize<T>(content);
        }

        private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string url, object body, bool includeAuth, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, url);

            if (includeAuth)
            {
                var accessToken = _tokenProvider.GetAccessToken();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }
            }

            if (body != null && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var json = JsonSerializer.Serialize(body);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return await _httpClient.SendAsync(request, ct);
        }

        private async Task ThrowNuvioHttpExceptionAsync(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            string code = null;
            string detail = null;
            string rawBody = null;

            try
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    try
                    {
                        var errorDoc = JsonDocument.Parse(content);
                        // JS parity (js/core/network/httpClient.js:78-86): string "code" → error.code,
                        // string "message" → error.detail; non-string values are skipped, not fatal.
                        if (errorDoc.RootElement.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.String)
                        {
                            code = codeElem.GetString();
                        }
                        if (errorDoc.RootElement.TryGetProperty("message", out var messageElem) && messageElem.ValueKind == JsonValueKind.String)
                        {
                            detail = messageElem.GetString();
                        }
                        else if (detail == null && errorDoc.RootElement.TryGetProperty("detail", out var detailElem) && detailElem.ValueKind == JsonValueKind.String)
                        {
                            detail = detailElem.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        // JS parity: keep the raw response text as the error message for non-JSON bodies.
                        rawBody = content;
                    }
                }
            }
            catch
            {
                // If content reading fails, use status message
                detail = response.ReasonPhrase;
            }

            throw new NuvioHttpException(status, code, detail, rawBody);
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
    }

    public static class JwtDecoder
    {
        public static long? GetExpirationSeconds(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                // Add padding if needed
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }

                var bytes = Convert.FromBase64String(payload);
                var json = Encoding.UTF8.GetString(bytes);
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("exp", out var expElem))
                {
                    return expElem.GetInt64();
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}