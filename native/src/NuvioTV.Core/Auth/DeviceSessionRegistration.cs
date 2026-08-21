using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Auth
{
    /// <summary>
    /// Registers the current device with the backend via RPC register_current_device
    /// while an authenticated session is active, at most once every 15 minutes.
    ///
    /// Behavioral spec (do not diverge): js/core/auth/deviceSessionRegistration.js.
    /// The installation id persists under the key "nuvio_web_installation_id"
    /// (name kept for backend continuity).
    /// </summary>
    public sealed class DeviceSessionRegistration : IDisposable
    {
        public static readonly TimeSpan RegistrationInterval = TimeSpan.FromMinutes(15);

        // JS parity constants (deviceSessionRegistration.js:8-16).
        internal const string ClientName = "Nuvio Web";
        internal const string InstallationIdKey = "nuvio_web_installation_id";
        internal const string InstallationIdPrefix = "nuvio-web-";
        internal const int InstallationIdLength = 32;
        internal const string InstallationIdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        private const int MaxPlatformLength = 80;
        private const int MaxDeviceNameLength = 160;
        private const int MaxClientVersionLength = 40;

        private readonly AuthManager _authManager;
        private readonly SupabaseClient _supabase;
        private readonly IKeyValueStore _store;
        private readonly IDeviceMetadata _metadata;
        private readonly string _clientVersion;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Action<string> _logWarning;
        private readonly object _gate = new object();

        private IDisposable _subscription;
        private Task<bool> _registrationPromise;
        private long _lastRegistrationAtMs;
        private CancellationTokenSource _loopCts;
        private Task _loopTask;
        private string _volatileInstallationId;
        private bool _disposed;

        /// <summary>Test seam: delay provider for the periodic registration loop.</summary>
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public DeviceSessionRegistration(
            HttpClient httpClient,
            AuthManager authManager,
            IKeyValueStore store,
            IDeviceMetadata metadata,
            string clientVersion = null,
            Func<DateTimeOffset> utcNow = null,
            Func<TimeSpan, CancellationToken, Task> delay = null,
            Action<string> logWarning = null)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            _authManager = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _supabase = new SupabaseClient(httpClient, authManager);
            _clientVersion = clientVersion ?? AppConfig.AppVersion ?? "0.0.0";
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
            _logWarning = logWarning;
        }

        /// <summary>
        /// Subscribes to auth-state changes (register immediately on Authenticated, reset
        /// the interval clock on SignedOut), performs an initial registration, and starts
        /// the periodic 15-minute loop. Returns after wiring; the loop runs in background.
        /// </summary>
        public Task StartAsync(CancellationToken ct = default)
        {
            EnsureSubscription();

            var initial = RegisterIfAuthenticatedAsync(true, ct);

            lock (_gate)
            {
                if (!_disposed && _loopTask == null)
                {
                    _loopCts = new CancellationTokenSource();
                    _loopTask = RunLoopAsync(_loopCts.Token);
                }
            }

            return initial;
        }

        private void EnsureSubscription()
        {
            lock (_gate)
            {
                if (_subscription != null)
                {
                    return;
                }
            }

            // Outside the lock: Subscribe fires the listener synchronously.
            _subscription = _authManager.Subscribe(state =>
            {
                if (state == AuthState.Authenticated)
                {
                    var ignored = RegisterIfAuthenticatedAsync(true);
                }
                else if (state == AuthState.SignedOut)
                {
                    lock (_gate)
                    {
                        _lastRegistrationAtMs = 0;
                    }
                }
            });
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _delay(RegistrationInterval, ct).ConfigureAwait(false);
                    await RegisterIfAuthenticatedAsync(false, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// JS parity: registerIfAuthenticated (deviceSessionRegistration.js:255-291) —
        /// single-flight, authenticated-only, throttled to one registration per interval
        /// unless forced.
        /// </summary>
        public Task<bool> RegisterIfAuthenticatedAsync(bool force = false, CancellationToken ct = default)
        {
            if (!_authManager.IsAuthenticated)
            {
                return Task.FromResult(false);
            }

            Task<bool> existing;
            lock (_gate)
            {
                existing = _registrationPromise;
                if (existing == null)
                {
                    var elapsedMs = _utcNow().ToUnixTimeMilliseconds() - _lastRegistrationAtMs;
                    var throttled = !force && _lastRegistrationAtMs > 0 && elapsedMs < RegistrationInterval.TotalMilliseconds;
                    if (!throttled)
                    {
                        existing = RunRegistrationAsync(ct);
                        _registrationPromise = existing;
                    }
                }
            }

            return existing ?? Task.FromResult(true);
        }

        private async Task<bool> RunRegistrationAsync(CancellationToken ct)
        {
            try
            {
                if (!_authManager.IsAuthenticated)
                {
                    return false;
                }

                var installationId = GetOrCreateInstallationId();
                var parameters = BuildRegistrationParams(installationId, _clientVersion, _metadata);
                await _supabase.RpcAsync("register_current_device", parameters, true, ct).ConfigureAwait(false);

                lock (_gate)
                {
                    _lastRegistrationAtMs = _utcNow().ToUnixTimeMilliseconds();
                }

                return true;
            }
            catch (Exception error)
            {
                // JS parity: registration failures are logged and swallowed.
                if (_logWarning != null)
                {
                    _logWarning("Device session registration failed: " + error.Message);
                }

                return false;
            }
            finally
            {
                lock (_gate)
                {
                    if (_registrationPromise != null && _registrationPromise.Id == Task.CurrentId)
                    {
                        _registrationPromise = null;
                    }
                }
            }
        }

        /// <summary>
        /// JS parity: buildDeviceRegistrationParams (deviceSessionRegistration.js:98-106).
        /// Exact RPC payload keys: p_installation_id, p_client_name, p_client_version,
        /// p_platform, p_device_name (null when empty).
        /// </summary>
        internal static Dictionary<string, object> BuildRegistrationParams(
            string installationId,
            string clientVersion,
            IDeviceMetadata metadata)
        {
            return new Dictionary<string, object>
            {
                ["p_installation_id"] = installationId,
                ["p_client_name"] = ClientName,
                ["p_client_version"] = Slice(Trim(clientVersion), MaxClientVersionLength),
                ["p_platform"] = Slice(FirstNonEmpty(metadata != null ? metadata.PlatformName : null, "Unknown"), MaxPlatformLength),
                ["p_device_name"] = NullIfEmpty(Slice(Trim(metadata != null ? metadata.DeviceName : null), MaxDeviceNameLength))
            };
        }

        /// <summary>
        /// JS parity: getOrCreateInstallationId (deviceSessionRegistration.js:74-96).
        /// Volatile cache → stored value when valid → generated and persisted under
        /// nuvio_web_installation_id.
        /// </summary>
        internal async Task<string> GetOrCreateInstallationIdAsync()
        {
            if (_volatileInstallationId != null)
            {
                return _volatileInstallationId;
            }

            string stored = null;
            try
            {
                stored = await _store.GetAsync(InstallationIdKey).ConfigureAwait(false);
            }
            catch
            {
                // Continue with an in-memory identity when persistent storage is unavailable.
            }

            if (IsValidInstallationId(stored))
            {
                _volatileInstallationId = stored.Trim();
                return _volatileInstallationId;
            }

            var generated = GenerateInstallationId();
            try
            {
                await _store.SetAsync(InstallationIdKey, generated).ConfigureAwait(false);
            }
            catch
            {
                // Registration still works for this app process when storage is unavailable.
            }

            _volatileInstallationId = generated;
            return _volatileInstallationId;
        }

        internal string GetOrCreateInstallationId()
        {
            return GetOrCreateInstallationIdAsync().GetAwaiter().GetResult();
        }

        // JS parity: isValidInstallationId (deviceSessionRegistration.js:50-53).
        internal static bool IsValidInstallationId(string value)
        {
            var normalized = Trim(value);
            if (normalized.Length < 16 || normalized.Length > 96)
            {
                return false;
            }

            foreach (var ch in normalized)
            {
                var valid = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        // JS parity: generateInstallationId (deviceSessionRegistration.js:55-72) —
        // 32 chars drawn from [a-z0-9] via byte % alphabet length, prefixed nuvio-web-.
        internal static string GenerateInstallationId(Action<byte[]> randomValues = null)
        {
            var bytes = new byte[InstallationIdLength];
            if (randomValues != null)
            {
                randomValues(bytes);
            }
            else
            {
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(bytes);
                }
            }

            var suffix = new char[InstallationIdLength];
            for (var index = 0; index < InstallationIdLength; index++)
            {
                suffix[index] = InstallationIdAlphabet[bytes[index] % InstallationIdAlphabet.Length];
            }

            return InstallationIdPrefix + new string(suffix);
        }

        private static string FirstNonEmpty(string value, string fallback)
        {
            var trimmed = Trim(value);
            return trimmed.Length > 0 ? trimmed : fallback;
        }

        private static string Trim(string value)
        {
            return (value ?? "").Trim();
        }

        private static string Slice(string value, int maxLength)
        {
            if (value == null || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength);
        }

        private static object NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? (object)null : value;
        }

        public void Dispose()
        {
            Task loopTask;
            CancellationTokenSource loopCts;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                loopTask = _loopTask;
                loopCts = _loopCts;
                _loopTask = null;
                _loopCts = null;
            }

            if (_subscription != null)
            {
                _subscription.Dispose();
                _subscription = null;
            }

            if (loopCts != null)
            {
                loopCts.Cancel();
                loopCts.Dispose();
            }

            if (loopTask != null)
            {
                try
                {
                    loopTask.Wait(1000);
                }
                catch
                {
                    // Loop teardown is best-effort.
                }
            }
        }
    }
}
