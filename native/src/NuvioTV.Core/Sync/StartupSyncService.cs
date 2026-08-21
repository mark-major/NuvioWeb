using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Boot-time and periodic sync orchestrator.
    ///
    /// Behavioral spec (do not diverge): js/core/profile/startupSyncService.js.
    /// Pulls profiles first, fans profile-scoped settings pulls out per known
    /// profile id, then runs the global pull chain (saved library → watched
    /// items → watch progress) when profile-scoped sync is enabled. Pushes run
    /// after pulls on the periodic cycle only. Pulls retry up to three times
    /// with a 3s pause; the cycle interval is 120s; addon-change pushes debounce
    /// at 1s. Services owned by other slices (plugins, credentials, settings,
    /// collections) plug in via <see cref="ExtraPullStepsAsync"/> /
    /// <see cref="ExtraPushStepsAsync"/> / <see cref="PullProfileSettingsStep"/>
    /// at composition time.
    /// </summary>
    public sealed class StartupSyncService : IDisposable
    {
        public const long SyncIntervalMs = 120000;
        public const int AddonPushDebounceMs = 1000;
        public const int MaxPullAttempts = 3;
        public const int PullRetryDelayMs = 3000;

        private readonly NuvioTV.Core.Auth.AuthManager _auth;
        private readonly ProfileSyncService _profilesSync;
        private readonly ProfileManager _profiles;
        private readonly SavedLibrarySyncService _savedLibrary;
        private readonly WatchedItemsSyncService _watchedItems;
        private readonly WatchProgressSyncService _watchProgress;
        private readonly Func<int, Task> _delay;

        private readonly object _gate = new object();
        private bool _started;
        private bool _profileScopedSyncEnabled;
        private bool _inFlight;
        private Timer _intervalTimer;

        private readonly List<Func<Task>> _extraPullSteps = new List<Func<Task>>();
        private readonly List<Func<Task>> _extraPushSteps = new List<Func<Task>>();

        /// <summary>
        /// Per-profile settings pull hook (JS ProfileSettingsSyncService.pull):
        /// receives the profile id, returns true when settings were applied.
        /// </summary>
        public Func<string, Task<bool>> PullProfileSettingsStep { get; set; }

        /// <summary>Invoked when a per-profile settings pull reported applied (JS I18n/ThemeManager reapply).</summary>
        public Action ProfileSettingsApplied { get; set; }

        public StartupSyncService(
            AuthManager auth,
            ProfileSyncService profilesSync,
            ProfileManager profiles,
            SavedLibrarySyncService savedLibrary,
            WatchedItemsSyncService watchedItems,
            WatchProgressSyncService watchProgress,
            Func<int, Task> delay = null)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _profilesSync = profilesSync ?? throw new ArgumentNullException(nameof(profilesSync));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _savedLibrary = savedLibrary ?? throw new ArgumentNullException(nameof(savedLibrary));
            _watchedItems = watchedItems ?? throw new ArgumentNullException(nameof(watchedItems));
            _watchProgress = watchProgress ?? throw new ArgumentNullException(nameof(watchProgress));
            _delay = delay ?? DefaultDelay;
        }

        private static Task DefaultDelay(int milliseconds)
        {
            return Task.Delay(milliseconds);
        }

        /// <summary>Registers an extra global pull step (collections, plugins, credentials…).</summary>
        public void AddExtraPullStep(Func<Task> step)
        {
            if (step != null)
            {
                lock (_gate)
                {
                    _extraPullSteps.Add(step);
                }
            }
        }

        /// <summary>Registers an extra global push step.</summary>
        public void AddExtraPushStep(Func<Task> step)
        {
            if (step != null)
            {
                lock (_gate)
                {
                    _extraPushSteps.Add(step);
                }
            }
        }

        // JS parity: start — idempotent; re-starting with profileScopedSyncEnabled
        // upgrades the flag but never double-schedules.
        public async Task StartAsync(bool profileScopedSyncEnabled = false, bool runInitialPull = true, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_started)
                {
                    if (profileScopedSyncEnabled)
                    {
                        _profileScopedSyncEnabled = true;
                    }

                    return;
                }

                _started = true;
                _profileScopedSyncEnabled = profileScopedSyncEnabled;
            }

            if (runInitialPull)
            {
                await SyncPullAsync(ct: ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _intervalTimer = new Timer(_ =>
                {
                    var unused = SyncCycleAsync();
                }, null, SyncIntervalMs, SyncIntervalMs);
            }
        }

        // JS parity: stop.
        public void Stop()
        {
            lock (_gate)
            {
                _started = false;
                _profileScopedSyncEnabled = false;
                if (_intervalTimer != null)
                {
                    _intervalTimer.Dispose();
                    _intervalTimer = null;
                }
            }
        }

        // JS parity: enableProfileScopedSync.
        public void EnableProfileScopedSync()
        {
            lock (_gate)
            {
                _profileScopedSyncEnabled = true;
            }
        }

        public bool IsStarted
        {
            get { lock (_gate) { return _started; } }
        }

        public bool IsProfileScopedSyncEnabled
        {
            get { lock (_gate) { return _profileScopedSyncEnabled; } }
        }

        // JS parity: requestSyncNow — single-flight; returns false when not
        // started or a cycle is already running.
        public async Task<bool> RequestSyncNowAsync(bool pushAfterPull = false, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (!_started || _inFlight)
                {
                    return false;
                }

                _inFlight = true;
            }

            try
            {
                bool includeProfileScoped;
                lock (_gate)
                {
                    includeProfileScoped = _profileScopedSyncEnabled;
                }

                await SyncPullAsync(includeProfileScoped, ct).ConfigureAwait(false);
                if (pushAfterPull && includeProfileScoped)
                {
                    await SyncPushAsync(ct).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight = false;
                }
            }
        }

        // JS parity: syncCycle.
        public Task<bool> SyncCycleAsync(CancellationToken ct = default)
        {
            return RequestSyncNowAsync(true, ct);
        }

        // JS parity: syncPull (startupSyncService.js:124-167).
        public async Task<bool> SyncPullAsync(bool? includeProfileScoped = null, CancellationToken ct = default)
        {
            bool scoped;
            lock (_gate)
            {
                scoped = includeProfileScoped.GetValueOrDefault(_profileScopedSyncEnabled);
            }

            if (!_auth.IsAuthenticated)
            {
                return false;
            }

            var didApplyProfileSettings = false;
            for (var attempt = 1; attempt <= MaxPullAttempts; attempt++)
            {
                try
                {
                    var profiles = await _profilesSync.PullAsync(ct).ConfigureAwait(false);
                    var profileIds = await CollectKnownProfileIdsAsync(profiles).ConfigureAwait(false);
                    foreach (var profileId in profileIds)
                    {
                        if (PullProfileSettingsStep == null)
                        {
                            continue;
                        }

                        didApplyProfileSettings =
                            (await PullProfileSettingsStep(profileId).ConfigureAwait(false)) || didApplyProfileSettings;
                    }

                    if (didApplyProfileSettings && ProfileSettingsApplied != null)
                    {
                        ProfileSettingsApplied();
                    }

                    Func<Task>[] extraPulls;
                    lock (_gate)
                    {
                        extraPulls = _extraPullSteps.ToArray();
                    }

                    foreach (var step in extraPulls)
                    {
                        await step().ConfigureAwait(false);
                    }

                    if (!scoped)
                    {
                        return didApplyProfileSettings;
                    }

                    await _savedLibrary.PullAsync(null, ct).ConfigureAwait(false);
                    await _watchedItems.PullAsync(null, ct).ConfigureAwait(false);
                    await _watchProgress.PullAsync(ct).ConfigureAwait(false);
                    return didApplyProfileSettings;
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    if (attempt < MaxPullAttempts)
                    {
                        await _delay(PullRetryDelayMs).ConfigureAwait(false);
                    }
                }
            }

            return didApplyProfileSettings;
        }

        // JS parity: syncPush (startupSyncService.js:169-188).
        public async Task SyncPushAsync(CancellationToken ct = default)
        {
            if (!_auth.IsAuthenticated)
            {
                return;
            }

            try
            {
                await _profilesSync.PushAsync(ct).ConfigureAwait(false);

                Func<Task>[] extraPushes;
                lock (_gate)
                {
                    extraPushes = _extraPushSteps.ToArray();
                }

                foreach (var step in extraPushes)
                {
                    await step().ConfigureAwait(false);
                }

                await _savedLibrary.PushAsync(null, ct).ConfigureAwait(false);
                await _watchedItems.PushAsync(null, ct).ConfigureAwait(false);
                await _watchProgress.PushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                // Swallowed like the JS console.warn path.
            }
        }

        // JS parity: scheduleAddonPush — 1s debounce; callers supply the actual
        // push action (LibrarySyncService.push lives in another slice).
        public Task ScheduleAddonPushAsync(Func<Task> pushAction, int? debounceMs = null)
        {
            var completion = new TaskCompletionSource<object>();
            lock (_gate)
            {
                if (!_started || !_profileScopedSyncEnabled)
                {
                    completion.TrySetResult(null);
                    return completion.Task;
                }

                var delay = Math.Max(0, debounceMs.GetValueOrDefault(AddonPushDebounceMs));
                var timer = new Timer(async _ =>
                {
                    try
                    {
                        if (pushAction != null && !_auth.IsAuthenticated)
                        {
                            completion.TrySetResult(null);
                            return;
                        }

                        if (pushAction != null)
                        {
                            await pushAction().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        completion.TrySetResult(null);
                    }
                }, null, delay, Timeout.Infinite);

                _addonPushTimer = timer;
            }

            return completion.Task;
        }

        private Timer _addonPushTimer;

        // JS parity: collectKnownProfileIds — active id plus pulled ids; falls
        // back to stored profiles when only one candidate exists.
        public async Task<IReadOnlyList<string>> CollectKnownProfileIdsAsync(IReadOnlyList<NuvioTV.Core.Models.UserProfile> profiles)
        {
            var ids = new List<string>();
            var activeId = await _profiles.GetActiveProfileId().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(activeId))
            {
                ids.Add(activeId);
            }

            foreach (var profile in profiles ?? new NuvioTV.Core.Models.UserProfile[0])
            {
                var id = NormalizeProfileId(profile.Id ?? profile.ProfileIndex.ToString());
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                }
            }

            if (ids.Count <= 1)
            {
                IReadOnlyList<NuvioTV.Core.Models.UserProfile> stored;
                try
                {
                    stored = await _profiles.GetProfilesAsync().ConfigureAwait(false);
                }
                catch
                {
                    stored = new NuvioTV.Core.Models.UserProfile[0];
                }

                foreach (var profile in stored)
                {
                    var id = NormalizeProfileId(profile.Id ?? profile.ProfileIndex.ToString());
                    if (!string.IsNullOrEmpty(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids.Distinct().ToList();
        }

        private static string NormalizeProfileId(string profileId)
        {
            var trimmed = (profileId ?? "").Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        public void Dispose()
        {
            Stop();
            lock (_gate)
            {
                if (_addonPushTimer != null)
                {
                    _addonPushTimer.Dispose();
                    _addonPushTimer = null;
                }
            }
        }
    }
}
