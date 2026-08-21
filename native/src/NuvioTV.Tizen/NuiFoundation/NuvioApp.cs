using System;
using System.IO;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Input;
using NuvioTV.Core.Localization;
using NuvioTV.Core.Settings;
using NuvioTV.Tizen.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.Screens;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Native NUI application shell. Boot order mirrors js/app.js
    /// bootstrapApp(): shell → config → storage → i18n → navigation/theme →
    /// device registration → auth bootstrap → auth-state routing (routing is
    /// completed by plan Task 18.2; this phase keeps the splash + stage labels).
    /// </summary>
    public sealed class NuvioApp : NUIApplication
    {
        private BootGuard _bootGuard;
        private SplashView _splash;
        private IDisposable _globalHandlers;
        private ScreenHost _screenHost;
        private Router _router;
        private FocusController _focusController = new FocusController();
        private HoldTimerService _holdTimer;
        private long _lastBackAtMs;

        protected override void OnCreate()
        {
            base.OnCreate();

            var window = GetDefaultWindow(); // fullscreen 1920x1080 on the target panel
            // Compatibility gate before any further NUI work (boot-guard.js parity).
            if (!RunCompatibilityGate(window))
            {
                Exit();
                return;
            }
            LoadCustomFonts();

            _splash = new SplashView();
            window.GetDefaultLayer().Add(_splash);

            _bootGuard = new BootGuard(window);
            _globalHandlers = BootGuard.InstallGlobalHandlers(
                () => "startup",
                (code, message, details) => _bootGuard.Fail(code, message, details));

            // Navigation shell (Task 9.2): single host view + router + key routing.
            _screenHost = new ScreenHost();
            window.GetDefaultLayer().Add(_screenHost);
            _router = new Router(_screenHost,
                route => new PlaceholderScreen(route.ToString()),
                () => Exit());
            AppServices.Router = _router;
            window.KeyEvent += OnWindowKeyEvent;
        }

        protected override void OnPause()
        {
            // Foreground lifecycle: webapp marks backgrounded on hidden; the
            // provider-credential foreground pull is wired in Task 18.2.
            base.OnPause();
        }

        protected override void OnResume()
        {
            // Task 18.2: ProviderCredentialSyncService.RequestForegroundPullAsync().
            base.OnResume();
        }

        protected override void OnTerminate()
        {
            _globalHandlers?.Dispose();
            AppServices.FileStore?.Dispose();
            base.OnTerminate();
        }

        private bool RunCompatibilityGate(Window window)
        {
            CompatibilityGate.Result result = null;
            try
            {
                result = CompatibilityGate.Check(
                    key => global::Tizen.System.Information.TryGetValue(key, out string value) ? value : null);
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "compatibility probe failed: " + ex.Message);
                return true; // fail open: allow boot rather than brick unknown devices
            }

            if (!result.Supported)
            {
                global::Tizen.Log.Error("NuvioTV",
                    $"unsupported platform version '{result.PlatformVersion}' — requires >= 5.5");
                return false;
            }
            global::Tizen.Log.Info("NuvioTV", $"platform {result.PlatformVersion} supported");
            return true;
        }

        /// <summary>
        /// Registers bundled OFL fonts (Resources/fonts) with the NUI font
        /// system so TextLabel.FontFamily can reference them by family name.
        /// Failure is non-fatal: NUI falls back to the platform font.
        /// </summary>
        private static void LoadCustomFonts()
        {
            var resDir = global::System.IO.Path.GetFullPath(global::System.IO.Path.Combine(
                global::Tizen.Applications.Application.Current.ApplicationInfo.ExecutablePath, "..", "..", "res"));
            var fontsDir = global::System.IO.Path.Combine(resDir, "fonts");
            if (!global::System.IO.Directory.Exists(fontsDir))
            {
                return;
            }
            try
            {
                // API7 exposes directory registration only (no per-file AddCustomFont).
                FontClient.Instance.AddCustomFontDirectory(fontsDir);
                global::Tizen.Log.Info("NuvioTV", "registered font directory " + fontsDir);
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "custom font registration failed: " + ex.Message);
            }
        }

        private async void RunBootSequence()
        {
            try
            {
                var appInfo = global::Tizen.Applications.Application.Current.ApplicationInfo;
                var installDir = global::System.IO.Path.GetFullPath(global::System.IO.Path.Combine(
                    global::Tizen.Applications.Application.Current.ApplicationInfo.ExecutablePath, ".."));
                var resourceDir = global::System.IO.Path.Combine(installDir, "..", "res");
                var dataDir = global::System.IO.Path.Combine(installDir, "..", "data");
                AppServices.Initialize(resourceDir, dataDir);
                _bootGuard.Stage("shell");

                _bootGuard.Stage("config");
                using (var stream = AppServices.OpenConfigStream())
                {
                    AppConfig.Load(stream);
                }
                global::Tizen.Log.Info("NuvioTV", $"config loaded (supabase host set: {AppConfig.SupabaseUrl.Length > 0})");

                _bootGuard.Stage("storage");
                await AppServices.FileStore.FlushAllAsync(); // warm the store cache

                _bootGuard.Stage("auth");
                I18n.Configure(AppServices.LoadLocaleJsonAsync, () => new[] { AppServices.GetSystemLocale() ?? "en_US" });
                await I18n.InitAsync(null);

                // Theme apply (js/app.js order: after Router init, before device registration).
                ThemeManager.Apply(await ThemeStore.GetAsync());

                await AppServices.DeviceRegistration.StartAsync();
                await AppServices.Auth.BootstrapAsync();

                SubscribeAuthStateRouting();

                // First route: placeholder Home until screens land (Phase 11+).
                await _router.NavigateAsync(Route.Home, new RouteParams(),
                    new NavigateOptions { SkipStackPush = true });
                _focusController.SetContainer(_screenHost);
                _focusController.SetInitialFocus();

                _bootGuard.Dismiss();
                if (_splash != null)
                {
                    var parent = _splash.GetParent();
                    parent?.Remove(_splash);
                    _splash.Dispose();
                    _splash = null;
                }

                global::Tizen.Log.Info("NuvioTV", $"bootstrap complete, auth state: {AppServices.Auth.State}");
            }
            catch (Exception ex)
            {
                _bootGuard.Fail("BOOT-APPLICATION",
                    "Something went wrong while the application was starting.",
                    ex.ToString());
            }
        }

        /// <summary>
        /// Auth-state routing skeleton (js/app.js subscribe block). Full routing
        /// lands in Task 18.2 once Router + screens exist.
        /// </summary>
        private void SubscribeAuthStateRouting()
        {
            AppServices.Auth.Subscribe(state =>
            {
                global::Tizen.Log.Info("NuvioTV", "auth state -> " + state);
                switch (state)
                {
                    case Core.Auth.AuthState.Loading:
                        _bootGuard.Stage("auth");
                        break;
                    case Core.Auth.AuthState.SignedOut:
                        _bootGuard.Stage("profile"); // placeholder until router exists
                        break;
                    case Core.Auth.AuthState.Authenticated:
                        _bootGuard.Stage("profile");
                        break;
                }
            });
        }

        /// <summary>
        /// Window key routing (Task 9.1 contract): normalize → dpad focus /
        /// back debounce → screen hooks. Media/letter keys reach screens only.
        /// </summary>
        private void OnWindowKeyEvent(object sender, Window.KeyEventArgs e)
        {
            var key = KeyMap.Normalize(e.Key);
            if (key == null) return;

            var isDown = e.Key.State == Key.StateType.Down;
            if (!isDown && e.Key.State != Key.StateType.Up)
            {
                return;
            }

            if (_holdTimer == null)
            {
                _holdTimer = new HoldTimerService(
                    () => global::Tizen.Log.Info("NuvioTV", "OK long-press"));
            }

            if (isDown)
            {
                if (HandleKeyDown(key.Value))
                {
                    return;
                }
                if (key.Value == NuvioKey.Ok)
                {
                    _holdTimer.OnKeyDown();
                }
            }
            else
            {
                if (key.Value == NuvioKey.Ok)
                {
                    _holdTimer.OnKeyUp();
                }
                HandleKeyUp(key.Value);
            }
        }

        private bool HandleKeyDown(NuvioKey key)
        {
            // Screen first: it may consume everything including dpad.
            var screen = _router.CurrentScreen;
            if (screen != null && screen.OnKeyDown(key))
            {
                return true;
            }

            switch (key)
            {
                case NuvioKey.Up:
                    return _focusController.MoveFocusDirectional(FocusDirection.Up);
                case NuvioKey.Down:
                    return _focusController.MoveFocusDirectional(FocusDirection.Down);
                case NuvioKey.Left:
                    return _focusController.MoveFocusDirectional(FocusDirection.Left);
                case NuvioKey.Right:
                    return _focusController.MoveFocusDirectional(FocusDirection.Right);
                case NuvioKey.Back:
                    HandleBackKeyDown();
                    return true;
            }
            return false;
        }

        private void HandleKeyUp(NuvioKey key)
        {
            if (_holdTimer != null && _holdTimer.ShouldSuppressClick())
            {
                return; // release after long-press is not a click
            }
            var screen = _router.CurrentScreen;
            if (screen == null) return;
            screen.OnKeyUp(key); // Ok click handling lives in the focused widget
        }

        /// <summary>Back 250ms debounce (Appendix C) then Router.BackAsync.</summary>
        private async void HandleBackKeyDown()
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastBackAtMs < 250)
            {
                return;
            }
            _lastBackAtMs = nowMs;
            await _router.BackAsync();
        }
    }
}
