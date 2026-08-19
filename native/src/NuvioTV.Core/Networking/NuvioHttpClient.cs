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

        public NuvioHttpException(int status, string code, string detail)
            : base(detail ?? $"HTTP error: {status}")
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

        public async Task<T> GetJsonAsync<T>(string url, CancellationToken ct = default)
        {
            return await SendJsonAsync<T>(HttpMethod.Get, url, null, ct);
        }

        public async Task<T> PostJsonAsync<T>(string url, object body, CancellationToken ct = default)
        {
            return await SendJsonAsync<T>(HttpMethod.Post, url, body, ct);
        }

        private async Task<T> SendJsonAsync<T>(HttpMethod method, string url, object body, CancellationToken ct)
        {
            var includeSessionAuth = _tokenProvider.HasTokens();

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

            try
            {
                var content = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    using (var jsonDoc = JsonDocument.Parse(content))
                    {
                        if (jsonDoc.RootElement.TryGetProperty("code", out var codeProp))
                        {
                            code = codeProp.GetString();
                        }
                        if (jsonDoc.RootElement.TryGetProperty("message", out var messageProp))
                        {
                            detail = messageProp.GetString();
                        }
                    }
                }
            }
            catch
            {
                // Keep default values on parse error
            }

            throw new NuvioHttpException(status, code, detail ?? $"HTTP {status}");
        }

        private bool IsTokenExpiringSoon(string token)
        {
            try
            {
                var expSeconds = JwtDecoder.GetExpirationSeconds(token);
                if (!expSeconds.HasValue)
                {
                    return false;
                }

                var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return expSeconds.Value - nowSeconds <= JwtExpirationLeewaySeconds;
            }
            catch
            {
                return false;
            }
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
            if (parts.Length < 2)
            {
                return null;
            }

            try
            {
                var payloadBase64 = parts[1];
                var payloadJson = Base64UrlDecode(payloadBase64);
                using (var jsonDoc = JsonDocument.Parse(payloadJson))
                {
                    if (jsonDoc.RootElement.TryGetProperty("exp", out var expProp))
                    {
                        return expProp.GetInt64();
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string Base64UrlDecode(string base64Url)
        {
            var s = base64Url.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            var bytes = Convert.FromBase64String(s);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}