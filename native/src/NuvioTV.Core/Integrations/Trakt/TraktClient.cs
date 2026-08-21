using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;

namespace NuvioTV.Core.Integrations.Trakt
{
    /// <summary>Device-flow poll outcome. JS parity: traktAuthService.js pollDeviceToken.</summary>
    public enum TraktPollResultType
    {
        Approved,
        Pending,
        AlreadyUsed,
        Expired,
        Denied,
        SlowDown,
        Failed
    }

    public sealed class TraktPollResult
    {
        public TraktPollResultType Type { get; set; }
        public int? PollIntervalSeconds { get; set; }
        public string Message { get; set; }

    }

    public sealed class TraktDeviceCode
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; }

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; }

        [JsonPropertyName("verification_url")]
        public string VerificationUrl { get; set; }

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    public sealed class TraktTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; }
    }

    /// <summary>
    /// Token state holder — persistence wiring (ProfileScopedStore envelope) lands with
    /// the sync tasks; the protocol layer only needs get/set.
    /// </summary>
    public interface ITraktAuthStateStore
    {
        TraktTokenResponse GetToken();
        void SaveToken(TraktTokenResponse token);
        void ClearAuth();
        void SaveDeviceFlow(TraktDeviceCode flow);
        TraktDeviceCode GetDeviceFlow();
        void ClearDeviceFlow();
        int PollIntervalSeconds { get; set; }
    }

    /// <summary>
    /// Protocol client for api.trakt.tv. Ports the request core of
    /// js/data/repository/traktAuthService.js: fixed headers (Content-Type
    /// application/json, trakt-api-version 2, trakt-api-key), device-code flow with
    /// Retry-After retry, token polling status mapping, refresh grant, and the
    /// paginated sync reads.
    /// </summary>
    public sealed class TraktClient
    {
        private const string DefaultApiUrl = "https://api.trakt.tv";
        private const string ApiVersion = "2";
        private const int RefreshLeewaySeconds = 60;

        private readonly HttpClient _httpClient;
        private readonly ITraktAuthStateStore _store;

        public TraktClient(HttpClient httpClient, ITraktAuthStateStore store)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // -------------------------------------------------------------------
        // Request core (traktAuthService.js requestJson, lines 38-61)
        // -------------------------------------------------------------------

        private async Task<TraktJsonResponse> RequestJsonAsync(
            string path, HttpMethod method, object body, string authorization, CancellationToken ct)
        {
            var url = ApiBaseUrl() + path;
            // Not disposed here: RecordingHandler in tests keeps the message for
            // post-call inspection; HttpClient does not dispose request messages.
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("trakt-api-version", ApiVersion);
            request.Headers.TryAddWithoutValidation("trakt-api-key", RequireClientId());
            if (!string.IsNullOrEmpty(authorization))
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }
            if (body != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new TraktJsonResponse((int)response.StatusCode, response.Headers, text);
        }

        private Task<TraktJsonResponse> GetAsync(string path, string token, CancellationToken ct)
        {
            return RequestJsonAsync(path, HttpMethod.Get, null, "Bearer " + token, ct);
        }

        private Task<TraktJsonResponse> PostAsync(string path, object body, string authorization, CancellationToken ct)
        {
            return RequestJsonAsync(path, HttpMethod.Post, body, authorization, ct);
        }

        // -------------------------------------------------------------------
        // Device auth flow (traktAuthService.js:104-224)
        // -------------------------------------------------------------------

        public async Task<TraktDeviceCode> StartDeviceAuthAsync(CancellationToken ct = default)
        {
            var response = await PostAsync(
                "/oauth/device/code", new { client_id = AppConfig.TraktClientId }, null, ct).ConfigureAwait(false);

            // JS parity (117-127): a single retry when Retry-After is 1..10 seconds.
            if (response.Status == 429)
            {
                var retryAfter = response.RetryAfterSeconds ?? 0;
                if (retryAfter >= 1 && retryAfter <= 10)
                {
                    await Task.Delay(retryAfter * 1000, ct).ConfigureAwait(false);
                    response = await PostAsync(
                        "/oauth/device/code", new { client_id = AppConfig.TraktClientId }, null, ct)
                        .ConfigureAwait(false);
                }
            }

            if (!response.IsSuccess)
            {
                if (response.Status == 429)
                {
                    var retryAfter = response.RetryAfterSeconds ?? 300;
                    var minutes = (int)Math.Ceiling(retryAfter / 60.0);
                    throw new TraktException(
                        "Trakt is rate limiting requests. Try again in ~" + minutes + " min");
                }
                throw new TraktException(response.ErrorMessage("Failed to start Trakt auth"));
            }

            var payload = response.Deserialize<TraktDeviceCode>();
            _store.SaveDeviceFlow(payload);
            return payload;
        }

        public async Task<TraktPollResult> PollDeviceTokenAsync(CancellationToken ct = default)
        {
            var flow = _store.GetDeviceFlow();
            if (flow == null || string.IsNullOrEmpty(flow.DeviceCode))
            {
                return new TraktPollResult { Type = TraktPollResultType.Failed, Message = "No active Trakt device code" };
            }

            var response = await PostAsync("/oauth/device/token", new
            {
                code = flow.DeviceCode,
                client_id = AppConfig.TraktClientId,
                client_secret = AppConfig.TraktClientSecret
            }, null, ct).ConfigureAwait(false);

            if (response.IsSuccess && response.HasPayload)
            {
                _store.SaveToken(response.Deserialize<TraktTokenResponse>());
                return new TraktPollResult { Type = TraktPollResultType.Approved };
            }

            switch (response.Status)
            {
                case 400:
                    return new TraktPollResult { Type = TraktPollResultType.Pending };
                case 409:
                    _store.ClearDeviceFlow();
                    return new TraktPollResult { Type = TraktPollResultType.AlreadyUsed };
                case 410:
                    _store.ClearDeviceFlow();
                    return new TraktPollResult { Type = TraktPollResultType.Expired };
                case 418:
                    _store.ClearDeviceFlow();
                    return new TraktPollResult { Type = TraktPollResultType.Denied };
                case 429:
                    // JS parity (203-207): interval grows by 5s, clamped to [5, 60].
                    var interval = Math.Min(60, Math.Max(5, _store.PollIntervalSeconds + 5));
                    _store.PollIntervalSeconds = interval;
                    return new TraktPollResult { Type = TraktPollResultType.SlowDown, PollIntervalSeconds = interval };
                default:
                    return new TraktPollResult
                    {
                        Type = TraktPollResultType.Failed,
                        Message = response.ErrorMessage("Token polling failed")
                    };
            }
        }

        /// <summary>
        /// JS parity refreshTokenIfNeeded (226-268): no-op when a valid unexpired token
        /// exists unless forced; on 401/403 clears auth.
        /// </summary>
        public async Task<bool> RefreshTokenIfNeededAsync(bool force = false, CancellationToken ct = default)
        {
            var token = _store.GetToken();
            if (token == null || string.IsNullOrEmpty(token.RefreshToken))
            {
                return false;
            }
            if (!force && !IsTokenExpiredOrExpiring(token))
            {
                return true;
            }

            var response = await PostAsync("/oauth/token", new
            {
                refresh_token = token.RefreshToken,
                client_id = AppConfig.TraktClientId,
                client_secret = AppConfig.TraktClientSecret,
                redirect_uri = RedirectUri(),
                grant_type = "refresh_token"
            }, null, ct).ConfigureAwait(false);

            if (!response.IsSuccess || !response.HasPayload)
            {
                if (response.Status == 401 || response.Status == 403)
                {
                    _store.ClearAuth();
                }
                return false;
            }

            _store.SaveToken(response.Deserialize<TraktTokenResponse>());
            return true;
        }

        /// <summary>JS parity getValidAccessToken (270-284).</summary>
        public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
        {
            var token = _store.GetToken();
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                return null;
            }
            if (IsTokenExpiredOrExpiring(token))
            {
                if (!await RefreshTokenIfNeededAsync(force: true, ct).ConfigureAwait(false))
                {
                    return null;
                }
                return _store.GetToken()?.AccessToken;
            }
            return token.AccessToken;
        }

        public async Task RevokeAsync(CancellationToken ct = default)
        {
            var token = _store.GetToken();
            if (token == null || string.IsNullOrEmpty(token.AccessToken))
            {
                _store.ClearAuth();
                return;
            }
            try
            {
                await PostAsync("/oauth/revoke", new
                {
                    token = token.AccessToken,
                    client_id = AppConfig.TraktClientId,
                    client_secret = AppConfig.TraktClientSecret
                }, null, ct).ConfigureAwait(false);
            }
            finally
            {
                _store.ClearAuth();
            }
        }

        // -------------------------------------------------------------------
        // Sync reads (traktAuthService.js:296-412)
        // -------------------------------------------------------------------

        /// <summary>Paginated walk; stops on short page (JS parity fetchWatchHistory).</summary>
        public async Task<IReadOnlyList<JsonElement>> FetchHistoryAsync(int limit = 100, CancellationToken ct = default)
        {
            return await FetchPaginatedAsync("/sync/history", limit, ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<JsonElement>> FetchWatchlistAsync(int limit = 100, CancellationToken ct = default)
        {
            return await FetchPaginatedAsync("/sync/watchlist", limit, ct).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<JsonElement>> FetchPlaybackStateAsync(int limit = 50, CancellationToken ct = default)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return Array.Empty<JsonElement>();
            }
            var response = await GetAsync("/sync/playback?limit=" + limit, token, ct).ConfigureAwait(false);
            return response.ArrayItemsOrEmpty();
        }

        public async Task<IReadOnlyList<JsonElement>> FetchWatchedShowsAsync(CancellationToken ct = default)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return Array.Empty<JsonElement>();
            }
            var response = await GetAsync("/sync/watched/shows", token, ct).ConfigureAwait(false);
            return response.ArrayItemsOrEmpty();
        }

        public async Task<IReadOnlyList<JsonElement>> FetchWatchedMoviesAsync(CancellationToken ct = default)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return Array.Empty<JsonElement>();
            }
            var userId = ResolveUserId() ?? "me";
            var response = await GetAsync(
                "/users/" + Uri.EscapeDataString(userId) + "/watched/movies?extended=noseasons",
                token, ct).ConfigureAwait(false);
            return response.ArrayItemsOrEmpty();
        }

        public async Task<JsonElement?> FetchStatsAsync(CancellationToken ct = default)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return null;
            }
            var userId = ResolveUserId() ?? "me";
            var response = await GetAsync("/users/" + Uri.EscapeDataString(userId) + "/stats", token, ct)
                .ConfigureAwait(false);
            return response.HasPayload ? response.Payload : default(JsonElement?);
        }

        public async Task<JsonElement?> FetchWatchedProgressAsync(string showTraktId, CancellationToken ct = default)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return null;
            }
            var response = await GetAsync(
                "/shows/" + Uri.EscapeDataString(showTraktId ?? "") + "/progress/watched",
                token, ct).ConfigureAwait(false);
            return response.HasPayload ? response.Payload : default(JsonElement?);
        }

        /// <summary>POST /scrobble/{action} (traktScrobbleService.js sendScrobbleRequest).</summary>
        public async Task<TraktJsonResponse> ScrobbleAsync(
            string action, object payload, CancellationToken ct = default)
        {
            var normalized = (action ?? "").Trim().ToLowerInvariant();
            if (normalized != "start" && normalized != "pause" && normalized != "stop")
            {
                throw new ArgumentException("Scrobble action must be start, pause, or stop", nameof(action));
            }
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                throw new TraktException("Not authenticated");
            }
            return await PostAsync("/scrobble/" + normalized, payload, "Bearer " + token, ct)
                .ConfigureAwait(false);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private async Task<IReadOnlyList<JsonElement>> FetchPaginatedAsync(
            string basePath, int limit, CancellationToken ct)
        {
            var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (token == null)
            {
                return Array.Empty<JsonElement>();
            }

            var all = new List<JsonElement>();
            var page = 1;
            var perPage = Math.Min(limit, 100);
            while (all.Count < limit)
            {
                var response = await GetAsync(
                    basePath + "?limit=" + perPage + "&page=" + page, token, ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    break;
                }
                var items = response.ArrayItemsOrEmpty();
                all.AddRange(items);
                if (items.Count < perPage)
                {
                    break;
                }
                page++;
            }
            return all.Take(limit).ToList();
        }

        private string ResolveUserId()
        {
            return null; // user slug persistence lands with the sync tasks; "me" works today
        }

        private static bool IsTokenExpiredOrExpiring(TraktTokenResponse token)
        {
            if (token.CreatedAt <= 0 || token.ExpiresIn <= 0)
            {
                return true;
            }
            var expiresAt = token.CreatedAt + token.ExpiresIn;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt - RefreshLeewaySeconds;
        }

        private static string ApiBaseUrl()
        {
            var configured = (AppConfig.TraktApiUrl ?? "").Trim().TrimEnd('/');
            return string.IsNullOrEmpty(configured) ? DefaultApiUrl : configured;
        }

        private static string RedirectUri()
        {
            return string.IsNullOrEmpty(AppConfig.TraktRedirectUri)
                ? "urn:ietf:wg:oauth:2.0:oob"
                : AppConfig.TraktRedirectUri;
        }

        private static string RequireClientId()
        {
            var clientId = AppConfig.TraktClientId;
            if (string.IsNullOrEmpty(clientId))
            {
                throw new TraktException("Missing TRAKT credentials");
            }
            return clientId;
        }
    }

    /// <summary>Raw HTTP outcome kept as text so callers can branch on status like the JS.</summary>
    public sealed class TraktJsonResponse
    {
        internal TraktJsonResponse(int status, System.Net.Http.Headers.HttpResponseHeaders headers, string bodyText)
        {
            Status = status;
            RetryAfterSeconds = ParseRetryAfter(headers);
            BodyText = bodyText ?? "";
        }

        public int Status { get; }
        public int? RetryAfterSeconds { get; }
        public string BodyText { get; }

        public bool IsSuccess => Status >= 200 && Status < 300;

        public bool HasPayload
        {
            get
            {
                var trimmed = BodyText.TrimStart();
                return trimmed.StartsWith("{") || trimmed.StartsWith("[");
            }
        }

        public T Deserialize<T>()
        {
            return JsonSerializer.Deserialize<T>(BodyText);
        }

        public JsonElement Payload => JsonSerializer.Deserialize<JsonElement>(BodyText);

        public IReadOnlyList<JsonElement> ArrayItemsOrEmpty()
        {
            if (!IsSuccess || !HasPayload)
            {
                return Array.Empty<JsonElement>();
            }
            var payload = Payload;
            if (payload.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<JsonElement>();
            }
            var items = new List<JsonElement>();
            foreach (var item in payload.EnumerateArray())
            {
                items.Add(item.Clone());
            }
            return items;
        }

        public string ErrorMessage(string fallback)
        {
            if (!HasPayload)
            {
                return fallback;
            }
            try
            {
                var payload = Payload;
                foreach (var key in new[] { "error_description", "error", "message" })
                {
                    if (payload.ValueKind == JsonValueKind.Object &&
                        payload.TryGetProperty(key, out var value) &&
                        value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(value.GetString()))
                    {
                        return value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
            }
            return fallback;
        }

        private static int? ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders headers)
        {
            if (headers != null && headers.RetryAfter != null && headers.RetryAfter.Delta.HasValue)
            {
                return (int)Math.Round(headers.RetryAfter.Delta.Value.TotalSeconds);
            }
            return null;
        }
    }

    public sealed class TraktException : Exception
    {
        public TraktException(string message) : base(message)
        {
        }
    }
}
