using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Auth
{
    /// <summary>
    /// Auth state machine: bootstrap from storage, password sign-in, sign-out,
    /// single-flight token refresh with transient-failure tolerance, and the
    /// effective-user-id RPC cache.
    ///
    /// Behavioral spec (do not diverge): js/core/auth/authManager.js.
    /// Token persistence uses the exact JS localStorage keys via
    /// <see cref="NuvioTV.Core.Storage.SessionStore"/>:
    /// access_token / refresh_token / is_anonymous_session.
    /// </summary>
    public sealed class AuthManager : ISessionTokenProvider
    {
        private const int JwtExpirationLeewaySeconds = 30;

        private readonly HttpClient _httpClient;
        private readonly SupabaseClient _supabase;
        private readonly IKeyValueStore _store;
        private readonly object _gate = new object();
        private readonly List<Action<AuthState>> _listeners = new List<Action<AuthState>>();

        private AuthState _state = AuthState.Loading;
        private string _accessToken;
        private string _refreshToken;
        private bool _isAnonymousSession;
        private bool _loadedFromStorage;
        private Task<bool> _refreshPromise;
        private string _lastRefreshFailureKind;
        private string _cachedEffectiveUserId;

        public AuthManager(HttpClient httpClient, IKeyValueStore store)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _supabase = new SupabaseClient(_httpClient, this);
        }

        public AuthState State => _state;

        // JS parity: get isAuthenticated (authManager.js:107-109).
        public bool IsAuthenticated => _state == AuthState.Authenticated;

        /// <summary>Whether the cached session is an anonymous one (qrLoginService parity).</summary>
        public bool IsAnonymousSession => _isAnonymousSession;

        /// <summary>Last refresh failure classification: null | rejected | invalid | transient | failed.</summary>
        public string LastRefreshFailureKind => _lastRefreshFailureKind;

        // -------------------------------------------------------------------
        // Subscribe (JS parity: subscribe, authManager.js:65-71 — fires immediately)
        // -------------------------------------------------------------------

        public IDisposable Subscribe(Action<AuthState> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            lock (_gate)
            {
                _listeners.Add(listener);
            }

            listener(_state);

            return new Subscription(this, listener);
        }

        // JS parity: setState broadcasts to every listener.
        public void SetState(AuthState newState)
        {
            _state = newState;

            Action<AuthState>[] snapshot;
            lock (_gate)
            {
                snapshot = _listeners.ToArray();
            }

            foreach (var listener in snapshot)
            {
                listener(newState);
            }
        }

        // -------------------------------------------------------------------
        // Bootstrap / Initialize (JS parity: bootstrap, authManager.js:81-101)
        // -------------------------------------------------------------------

        /// <summary>Alias kept for call sites that name the boot step Initialize.</summary>
        public Task BootstrapAsync(CancellationToken ct = default)
        {
            return InitializeAsync(ct);
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            await LoadStoredSessionAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(_accessToken))
            {
                SetState(AuthState.SignedOut);
                return;
            }

            // Anonymous sessions never bootstrap into the authenticated UI.
            if (_isAnonymousSession)
            {
                SetState(AuthState.SignedOut);
                return;
            }

            var refreshed = await RefreshSessionIfNeededAsync(false, ct).ConfigureAwait(false);
            if (!refreshed)
            {
                SetState(AuthState.SignedOut);
                return;
            }

            SetState(AuthState.Authenticated);
        }

        private async Task LoadStoredSessionAsync()
        {
            if (_loadedFromStorage)
            {
                return;
            }

            _accessToken = await SessionStore.GetAccessTokenAsync(_store).ConfigureAwait(false);
            _refreshToken = await SessionStore.GetRefreshTokenAsync(_store).ConfigureAwait(false);
            _isAnonymousSession = await SessionStore.GetIsAnonymousSessionAsync(_store).ConfigureAwait(false);
            _loadedFromStorage = true;
        }

        // -------------------------------------------------------------------
        // Email login (JS parity: signInWithEmail, authManager.js:122-141)
        // -------------------------------------------------------------------

        public async Task SignInWithEmailAsync(string email, string password, CancellationToken ct = default)
        {
            // Throws NuvioHttpException on non-OK (JS: `throw new Error("Login failed")`).
            var data = await _supabase.PasswordTokenAsync(email, password, ct).ConfigureAwait(false);

            await StoreTokensAsync(
                GetStringProperty(data, "access_token"),
                GetStringProperty(data, "refresh_token"),
                false,
                ct).ConfigureAwait(false);

            SetState(AuthState.Authenticated);
        }

        // -------------------------------------------------------------------
        // Sign out (JS parity: signOut, authManager.js:143-148)
        // -------------------------------------------------------------------

        public async Task SignOutAsync()
        {
            await ClearSessionAsync().ConfigureAwait(false);
            _cachedEffectiveUserId = null;
            SetState(AuthState.SignedOut);
        }

        /// <summary>
        /// Clears stored + in-memory session state without notifying listeners.
        /// QR session recovery clears tokens without touching the auth state machine
        /// (JS parity: qrLoginService.js writes SessionStore directly).
        /// </summary>
        public async Task ClearSessionAsync()
        {
            _accessToken = null;
            _refreshToken = null;
            _isAnonymousSession = false;
            await SessionStore.ClearAsync(_store).ConfigureAwait(false);
        }

        /// <summary>
        /// Single write path for token rotation. Persists under the exact JS store keys
        /// and keeps the in-memory snapshot coherent for ISessionTokenProvider consumers.
        /// </summary>
        public async Task StoreTokensAsync(string accessToken, string refreshToken, bool isAnonymousSession, CancellationToken ct = default)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _isAnonymousSession = isAnonymousSession;

            await SessionStore.SetAccessTokenAsync(_store, accessToken).ConfigureAwait(false);
            await SessionStore.SetRefreshTokenAsync(_store, refreshToken).ConfigureAwait(false);
            await SessionStore.SetIsAnonymousSessionAsync(_store, isAnonymousSession).ConfigureAwait(false);
        }

        // -------------------------------------------------------------------
        // Refresh (JS parity: refreshSessionIfNeeded, authManager.js:150-205)
        // -------------------------------------------------------------------

        public Task<bool> RefreshSessionIfNeededAsync(bool force = false, CancellationToken ct = default)
        {
            Task<bool> existing;
            lock (_gate)
            {
                existing = _refreshPromise;
            }

            if (existing != null)
            {
                return existing;
            }

            _lastRefreshFailureKind = null;

            var accessToken = _accessToken;
            var refreshToken = _refreshToken;

            // No refresh token: valid only while the access JWT is still unexpired (0 leeway).
            if (string.IsNullOrEmpty(refreshToken))
            {
                return Task.FromResult(!string.IsNullOrEmpty(accessToken) && !IsJwtExpired(accessToken, 0));
            }

            if (!force && !string.IsNullOrEmpty(accessToken) && !IsJwtExpired(accessToken, JwtExpirationLeewaySeconds))
            {
                return Task.FromResult(true);
            }

            var task = RunRefreshAsync(accessToken, refreshToken, ct);
            lock (_gate)
            {
                _refreshPromise = task;
            }

            return task;
        }

        private async Task<bool> RunRefreshAsync(string accessToken, string refreshToken, CancellationToken ct)
        {
            try
            {
                JsonElement data;
                try
                {
                    data = await _supabase.RefreshTokenAsync(refreshToken, ct).ConfigureAwait(false);
                }
                catch (NuvioHttpException)
                {
                    // JS: `if (!res.ok) { lastRefreshFailureKind = "rejected"; return false; }`
                    _lastRefreshFailureKind = "rejected";
                    return false;
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    if (IsTransientNetworkError(error) && !string.IsNullOrEmpty(accessToken))
                    {
                        _lastRefreshFailureKind = "transient";
                        return true;
                    }

                    _lastRefreshFailureKind = "failed";
                    return false;
                }

                var newAccessToken = GetStringProperty(data, "access_token");
                if (string.IsNullOrEmpty(newAccessToken))
                {
                    _lastRefreshFailureKind = "invalid";
                    return false;
                }

                await StoreTokensAsync(
                    newAccessToken,
                    GetStringProperty(data, "refresh_token") ?? _refreshToken,
                    _isAnonymousSession,
                    ct).ConfigureAwait(false);

                _lastRefreshFailureKind = null;
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    // Clear only while this task is still the registered single-flight entry,
                    // so a newer refresh started meanwhile is never clobbered.
                    if (_refreshPromise != null && _refreshPromise.Id == Task.CurrentId)
                    {
                        _refreshPromise = null;
                    }
                }
            }
        }

        // JS parity: wasLastSessionRefreshTransientFailure (authManager.js:111-113).
        public bool WasLastSessionRefreshTransientFailure()
        {
            return _lastRefreshFailureKind == "transient";
        }

        // JS parity: isAccessTokenExpired (authManager.js:115-117).
        public bool IsAccessTokenExpired(int leewaySeconds = JwtExpirationLeewaySeconds)
        {
            return IsJwtExpired(_accessToken, leewaySeconds);
        }

        // JS parity: isTransientNetworkError (authManager.js:38-50). .NET folds transport
        // failures into HttpRequestException chains, so the message scan walks the inner-
        // exception chain; HttpRequestException itself counts as a network failure.
        private static bool IsTransientNetworkError(Exception error)
        {
            if (error == null)
            {
                return false;
            }

            var name = error.GetType().Name.ToLowerInvariant();
            if (name == "typeerror" || name == "aborterror")
            {
                return true;
            }

            if (error is HttpRequestException)
            {
                return true;
            }

            var parts = new List<string>();
            for (var current = error; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrEmpty(current.Message))
                {
                    parts.Add(current.Message);
                }
            }

            var message = string.Join(" ", parts).ToLowerInvariant();
            return message.Contains("failed to fetch")
                || message.Contains("network")
                || message.Contains("load failed")
                || message.Contains("internet")
                || message.Contains("offline");
        }

        // JS parity helpers (authManager.js:6-36).
        internal static bool IsJwtLike(string token)
        {
            var value = (token ?? "").Trim();
            return value.Split('.').Length == 3;
        }

        // Missing or unparsable exp means NOT expired (JS: `exp <= 0 → false`).
        internal static bool IsJwtExpired(string token, int leewaySeconds)
        {
            if (!IsJwtLike(token))
            {
                return true;
            }

            long? exp;
            try
            {
                exp = JwtDecoder.GetExpirationSeconds(token);
            }
            catch
            {
                return true;
            }

            if (!exp.HasValue || exp.Value <= 0)
            {
                return false;
            }

            var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return exp.Value <= nowSeconds + leewaySeconds;
        }

        // -------------------------------------------------------------------
        // Effective user id (JS parity: getEffectiveUserId, authManager.js:280-327)
        // -------------------------------------------------------------------

        public async Task<string> GetEffectiveUserIdAsync(CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(_cachedEffectiveUserId))
            {
                return _cachedEffectiveUserId;
            }

            await LoadStoredSessionAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(GetAccessToken()))
            {
                var refreshed = await RefreshSessionIfNeededAsync(false, ct).ConfigureAwait(false);
                if (!refreshed || string.IsNullOrEmpty(GetAccessToken()))
                {
                    await SignOutAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("Missing valid session token");
                }
            }

            JsonElement data;
            try
            {
                // JS sends a bodyless POST here (headers only).
                data = await _supabase.RpcAsync("get_sync_owner", null, true, ct).ConfigureAwait(false);
            }
            catch (NuvioHttpException ex) when (ex.Status == 401)
            {
                await SignOutAsync().ConfigureAwait(false);
                throw;
            }

            var id = ScalarToString(data);
            _cachedEffectiveUserId = id;
            return id;
        }

        private static string ScalarToString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetRawText();
                default:
                    return element.ValueKind == JsonValueKind.Undefined ? null : element.GetRawText();
            }
        }

        private static string GetStringProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Null || property.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                return NormalizeToken(value);
            }

            return null;
        }

        // JS parity: SessionStore.normalizeToken (sessionStore.js:2-8).
        private static string NormalizeToken(string value)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text) || text == "null" || text == "undefined")
            {
                return null;
            }

            return text;
        }

        // -------------------------------------------------------------------
        // ISessionTokenProvider (consumed by SupabaseClient / NuvioHttpClient)
        // -------------------------------------------------------------------

        public string GetAccessToken() => _accessToken;

        public string GetRefreshToken() => _refreshToken;

        public bool HasTokens() => !string.IsNullOrEmpty(_accessToken) && !string.IsNullOrEmpty(_refreshToken);

        public Task<bool> TryRefreshAsync(bool force = false)
        {
            return RefreshSessionIfNeededAsync(force);
        }

        private sealed class Subscription : IDisposable
        {
            private AuthManager _owner;
            private readonly Action<AuthState> _listener;

            internal Subscription(AuthManager owner, Action<AuthState> listener)
            {
                _owner = owner;
                _listener = listener;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner == null)
                {
                    return;
                }

                lock (owner._gate)
                {
                    owner._listeners.Remove(_listener);
                }
            }
        }
    }
}
