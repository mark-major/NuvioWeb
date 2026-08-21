using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Integrations.Simkl
{
    /// <summary>
    /// Injectable clock used for client-side rate limiting and backoff sleeps.
    /// Tests substitute a virtual clock that advances instantly.
    /// </summary>
    public interface ISimklClock
    {
        long UtcNowMs();

        Task DelayAsync(long milliseconds, CancellationToken ct);
    }

    /// <summary>
    /// Wall-clock implementation backed by DateTimeOffset.UtcNow and Task.Delay.
    /// </summary>
    public sealed class SimklSystemClock : ISimklClock
    {
        public static readonly SimklSystemClock Instance = new SimklSystemClock();

        public long UtcNowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public Task DelayAsync(long milliseconds, CancellationToken ct)
        {
            if (milliseconds <= 0)
            {
                return Task.CompletedTask;
            }
            return Task.Delay((int)Math.Min(milliseconds, int.MaxValue), ct);
        }
    }

    /// <summary>
    /// Descriptor for a single Simkl API request. Mirrors the option bag of
    /// js/data/repository/simklAuthService.js simklRequest(path, options).
    /// </summary>
    public sealed class SimklRequest
    {
        public string Path { get; set; }
        public string Method { get; set; } = "GET";
        public object Body { get; set; }
        public bool Authenticated { get; set; } = true;
        public string Token { get; set; }
        public bool Retry { get; set; } = true;

        public SimklRequest()
        {
        }

        public SimklRequest(string path)
        {
            Path = path;
        }

        public SimklRequest WithMethod(string method)
        {
            Method = method;
            return this;
        }

        public SimklRequest WithBody(object body)
        {
            Body = body;
            return this;
        }

        public SimklRequest WithToken(string token)
        {
            Authenticated = true;
            Token = token;
            return this;
        }

        public SimklRequest Unauthenticated()
        {
            Authenticated = false;
            Token = null;
            return this;
        }

        public SimklRequest WithoutRetry()
        {
            Retry = false;
            return this;
        }
    }

    // ---------------------------------------------------------------------------
    // Response DTOs. Field names match the Simkl wire format exactly.
    // ---------------------------------------------------------------------------

    public sealed class SimklPinStartResult
    {
        [JsonPropertyName("result")]
        public string Result { get; set; }

        [JsonPropertyName("user_code")]
        public string UserCode { get; set; }

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; }

        [JsonPropertyName("verification_url")]
        public string VerificationUrl { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }
    }

    public sealed class SimklPinPollResponse
    {
        [JsonPropertyName("result")]
        public string Result { get; set; }

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("device_code")]
        public string DeviceCode { get; set; }
    }

    public sealed class SimklUserSettings
    {
        [JsonPropertyName("user")]
        public SimklUserSettingsUser User { get; set; }

        [JsonPropertyName("account")]
        public SimklUserSettingsAccount Account { get; set; }
    }

    public sealed class SimklUserSettingsUser
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public sealed class SimklUserSettingsAccount
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    /// <summary>
    /// Scrobble media object: { title, year, ids }. Ids keys follow Simkl's
    /// external id vocabulary (simkl, imdb, tmdb, tvdb, mal, anidb, anilist, kitsu).
    /// </summary>
    public sealed class SimklScrobbleMedia
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("year")]
        public long? Year { get; set; }

        [JsonPropertyName("ids")]
        public System.Collections.Generic.IDictionary<string, object> Ids { get; set; }
    }

    public sealed class SimklScrobbleEpisode
    {
        [JsonPropertyName("season")]
        public long? Season { get; set; }

        [JsonPropertyName("number")]
        public long Number { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }
    }

    /// <summary>
    /// Scrobble payload: { progress, movie?|show?|anime?, episode? }.
    /// Source: js/data/repository/simklScrobbleService.js buildPayload
    /// </summary>
    public sealed class SimklScrobblePayload
    {
        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("movie")]
        public SimklScrobbleMedia Movie { get; set; }

        [JsonPropertyName("show")]
        public SimklScrobbleMedia Show { get; set; }

        [JsonPropertyName("anime")]
        public SimklScrobbleMedia Anime { get; set; }

        [JsonPropertyName("episode")]
        public SimklScrobbleEpisode Episode { get; set; }
    }

    /// <summary>
    /// Protocol client for api.simkl.com. Ports the request core of
    /// js/data/repository/simklAuthService.js: shared query parameters,
    /// serialized execution, client-side rate limiting (GET 100ms / write 1s),
    /// and 5-attempt exponential backoff for transient failures.
    /// </summary>
    public sealed class SimklClient
    {
        private const string DefaultApiUrl = "https://api.simkl.com";
        private const int MaxAttempts = 5;
        private const int GetIntervalMs = 100;
        private const int WriteIntervalMs = 1000;
        private const long MaxBackoffMs = 60000;
        private const string StopScrobblePath = "/scrobble/stop";

        private readonly HttpClient _httpClient;
        private readonly ISimklClock _clock;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private long _nextGetAtMs;
        private long _nextWriteAtMs;

        public SimklClient(HttpClient httpClient) : this(httpClient, null)
        {
        }

        public SimklClient(HttpClient httpClient, ISimklClock clock)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _clock = clock ?? SimklSystemClock.Instance;
        }

        // -----------------------------------------------------------------------
        // Typed endpoint helpers
        // -----------------------------------------------------------------------

        /// <summary>GET /oauth/pin (unauthenticated).</summary>
        public Task<SimklPinStartResult> StartPinAsync(CancellationToken ct = default)
        {
            return SendForAsync<SimklPinStartResult>(new SimklRequest("/oauth/pin").Unauthenticated(), ct);
        }

        /// <summary>GET /oauth/pin/{user_code} (unauthenticated, no retry).</summary>
        public Task<SimklPinPollResponse> PollPinAsync(string userCode, CancellationToken ct = default)
        {
            var encoded = Uri.EscapeDataString(userCode ?? "");
            return SendForAsync<SimklPinPollResponse>(
                new SimklRequest("/oauth/pin/" + encoded).Unauthenticated().WithoutRetry(), ct);
        }

        /// <summary>POST /users/settings (authenticated).</summary>
        public Task<SimklUserSettings> PostUserSettingsAsync(string token, CancellationToken ct = default)
        {
            return SendForAsync<SimklUserSettings>(
                new SimklRequest("/users/settings").WithMethod("POST").WithToken(token), ct);
        }

        /// <summary>
        /// GET /sync/all-items/{shows|movies|anime}?extended=full_anime_seasons&amp;...
        /// Query family matches js/data/repository/simklSyncService.js EXTENDED_QUERY.
        /// </summary>
        public Task<JsonElement> GetAllItemsAsync(string token, string mediaType, CancellationToken ct = default)
        {
            var normalized = (mediaType ?? "").Trim().ToLowerInvariant();
            if (normalized != "shows" && normalized != "movies" && normalized != "anime")
            {
                throw new ArgumentException(
                    "Simkl all-items media type must be shows, movies, or anime", nameof(mediaType));
            }
            var request = new SimklRequest("/sync/all-items/" + normalized)
            {
                Path = "/sync/all-items/" + normalized +
                       "?extended=full_anime_seasons" +
                       "&episode_watched_at=yes" +
                       "&episode_tvdb_id=yes" +
                       "&include_all_episodes=yes" +
                       "&language=en",
                Token = token
            };
            return SendAsync(request, ct);
        }

        /// <summary>GET /sync/activities (authenticated).</summary>
        public Task<JsonElement> GetActivitiesAsync(string token, CancellationToken ct = default)
        {
            return SendAsync(new SimklRequest("/sync/activities").WithToken(token), ct);
        }

        /// <summary>GET /sync/playback (authenticated).</summary>
        public Task<JsonElement> GetPlaybackAsync(string token, CancellationToken ct = default)
        {
            return SendAsync(new SimklRequest("/sync/playback").WithToken(token), ct);
        }

        /// <summary>
        /// POST /scrobble/{start|pause|stop} (authenticated, no retry).
        /// A 409 on /scrobble/stop is treated as success, matching the webapp.
        /// </summary>
        public Task<JsonElement> ScrobbleAsync(
            string token, string action, SimklScrobblePayload payload, CancellationToken ct = default)
        {
            var normalized = (action ?? "").Trim().ToLowerInvariant();
            if (normalized != "start" && normalized != "pause" && normalized != "stop")
            {
                throw new ArgumentException(
                    "Simkl scrobble action must be start, pause, or stop", nameof(action));
            }
            return SendAsync(
                new SimklRequest(StopScrobblePath.Replace("stop", normalized))
                    .WithMethod("POST")
                    .WithBody(payload)
                    .WithToken(token)
                    .WithoutRetry(),
                ct);
        }

        // -----------------------------------------------------------------------
        // Core request pipeline
        // -----------------------------------------------------------------------

        /// <summary>
        /// Executes a request and returns the parsed JSON payload as JsonElement
        /// (ValueKind.Undefined when the body is empty or not JSON).
        /// </summary>
        public async Task<JsonElement> SendAsync(SimklRequest request, CancellationToken ct = default)
        {
            var payload = await SendCoreAsync(request, ct).ConfigureAwait(false);
            return payload;
        }

        /// <summary>
        /// Executes a request and deserializes the JSON payload into T.
        /// Returns default(T) when the body is empty or not JSON.
        /// </summary>
        public async Task<T> SendForAsync<T>(SimklRequest request, CancellationToken ct = default)
        {
            var payload = await SendCoreAsync(request, ct).ConfigureAwait(false);
            if (payload.ValueKind == JsonValueKind.Object || payload.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<T>(payload.GetRawText(), LocalStore.JsonOptions);
            }
            return default(T);
        }

        private async Task<JsonElement> SendCoreAsync(SimklRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            EnsureClientId();
            if (request.Authenticated && string.IsNullOrEmpty(request.Token))
            {
                throw new InvalidOperationException("Simkl authentication required");
            }

            var url = BuildUrl(request.Path);
            var isGet = string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase);
            var bodyJson = request.Body == null
                ? null
                : JsonSerializer.Serialize(request.Body, LocalStore.JsonOptions);

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var attempts = request.Retry ? MaxAttempts : 1;
                for (var attempt = 0; ; attempt++)
                {
                    HttpResponseMessage response;
                    try
                    {
                        response = await ExecuteGatedFetchAsync(request, url, bodyJson, isGet, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        if (attempt + 1 >= attempts)
                        {
                            throw;
                        }
                        await _clock.DelayAsync(NetworkBackoffMs(attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    var status = (int)response.StatusCode;
                    var payload = await ReadPayloadAsync(response).ConfigureAwait(false);
                    response.Dispose();

                    if (response.IsSuccessStatusCode || (status == 409 && request.Path == StopScrobblePath))
                    {
                        return payload;
                    }
                    if (status == 401)
                    {
                        throw new NuvioHttpException(401, "unauthorized", "Simkl authorization was revoked");
                    }

                    var syncWriteLocked =
                        status == 400 &&
                        !isGet &&
                        request.Path != null &&
                        request.Path.StartsWith("/sync/", StringComparison.Ordinal) &&
                        PayloadErrorIs(payload, "rate_limit");
                    var transient = syncWriteLocked ||
                                    status == 429 || status == 500 || status == 502 || status == 503;
                    if (!transient || attempt + 1 >= attempts)
                    {
                        throw BuildHttpError(status, payload);
                    }

                    var retryAfterMs = RetryAfterMs(response);
                    var delay = Math.Min(
                        MaxBackoffMs,
                        Math.Max(retryAfterMs, syncWriteLocked ? 3000 : 1000L * (1L << attempt)));
                    await _clock.DelayAsync(delay, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Waits for the per-method-class rate-limit gate, performs the fetch, and
        /// updates the gate timestamp afterwards — mirroring rateLimitedFetch in
        /// simklAuthService.js (the gate updates even when the fetch fails).
        /// </summary>
        private async Task<HttpResponseMessage> ExecuteGatedFetchAsync(
            SimklRequest request, string url, string bodyJson, bool isGet, CancellationToken ct)
        {
            var scheduledAt = isGet ? _nextGetAtMs : _nextWriteAtMs;
            var now = _clock.UtcNowMs();
            if (scheduledAt > now)
            {
                await _clock.DelayAsync(scheduledAt - now, ct).ConfigureAwait(false);
            }

            var fetchStarted = false;
            try
            {
                fetchStarted = true;
                return await SendHttpRequestAsync(request, url, bodyJson, ct).ConfigureAwait(false);
            }
            finally
            {
                if (fetchStarted)
                {
                    UpdateGate(isGet);
                }
            }
        }

        private void UpdateGate(bool isGet)
        {
            var interval = isGet ? GetIntervalMs : WriteIntervalMs;
            var nextAt = _clock.UtcNowMs() + interval;
            if (isGet)
            {
                _nextGetAtMs = Math.Max(_nextGetAtMs, nextAt);
            }
            else
            {
                _nextWriteAtMs = Math.Max(_nextWriteAtMs, nextAt);
            }
        }

        private async Task<HttpResponseMessage> SendHttpRequestAsync(
            SimklRequest request, string url, string bodyJson, CancellationToken ct)
        {
            var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method.ToUpperInvariant()), url);
            httpRequest.Headers.Accept.ParseAdd("application/json");
            httpRequest.Headers.Add("simkl-client-id", ClientId());
            if (request.Authenticated && !string.IsNullOrEmpty(request.Token))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Token);
            }
            if (bodyJson != null &&
                !string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            }
            return await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        }

        private static async Task<JsonElement> ReadPayloadAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(text))
            {
                return default(JsonElement);
            }
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            {
                return default(JsonElement);
            }
            try
            {
                using (var document = JsonDocument.Parse(text))
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                return default(JsonElement);
            }
        }

        // -----------------------------------------------------------------------
        // URL construction (query-parameter order matches the webapp exactly)
        // -----------------------------------------------------------------------

        private string BuildUrl(string path)
        {
            var relative = string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal)
                ? "/" + (path ?? "")
                : path;
            var separator = relative.IndexOf('?') >= 0 ? '&' : '?';
            return ApiBaseUrl() + relative + separator +
                   "client_id=" + Uri.EscapeDataString(ClientId()) +
                   "&app-name=" + Uri.EscapeDataString(AppName()) +
                   "&app-version=" + Uri.EscapeDataString(AppVersion());
        }

        private static string ApiBaseUrl()
        {
            var configured = (AppConfig.SimklApiUrl ?? "").Trim().TrimEnd('/');
            return string.IsNullOrEmpty(configured) ? DefaultApiUrl : configured;
        }

        private static string ClientId()
        {
            return AppConfig.SimklClientId ?? "";
        }

        private static string AppName()
        {
            var name = AppConfig.SimklAppName ?? "";
            return string.IsNullOrEmpty(name) ? "nuvio" : name;
        }

        private static string AppVersion()
        {
            var version = AppConfig.AppVersion ?? "";
            return string.IsNullOrEmpty(version) ? "0.0.0" : version;
        }

        private static void EnsureClientId()
        {
            if (string.IsNullOrWhiteSpace(AppConfig.SimklClientId))
            {
                throw new InvalidOperationException("Missing SIMKL_CLIENT_ID");
            }
        }

        // -----------------------------------------------------------------------
        // Failure classification helpers
        // -----------------------------------------------------------------------

        private static long NetworkBackoffMs(int attempt)
        {
            return Math.Min(MaxBackoffMs, 1000L * (1L << attempt));
        }

        private static bool PayloadErrorIs(JsonElement payload, string expected)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!payload.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            return string.Equals(error.GetString(), expected, StringComparison.OrdinalIgnoreCase);
        }

        private static long RetryAfterMs(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return 0;
            }
            double seconds = 0;
            if (retryAfter.Delta.HasValue)
            {
                seconds = retryAfter.Delta.Value.TotalSeconds;
            }
            else if (retryAfter.Date.HasValue)
            {
                seconds = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
            }
            return seconds > 0 ? (long)(seconds * 1000) : 0;
        }

        private static NuvioHttpException BuildHttpError(int status, JsonElement payload)
        {
            string detail;
            string code = null;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                var message = FirstStringProperty(payload, "message");
                var errorDescription = FirstStringProperty(payload, "error_description");
                var error = FirstStringProperty(payload, "error");
                detail = FirstNonEmpty(message, errorDescription, error) ?? ("HTTP " + status);
                code = error;
            }
            else
            {
                detail = "Simkl request failed (" + status + ")";
            }
            return new NuvioHttpException(status, code, detail);
        }

        private static string FirstStringProperty(JsonElement payload, string name)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }
    }
}
