using System;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Account
{
    /// <summary>
    /// Port of authQrSignInScreen.js: QR card + status line, poll loop until
    /// approved/expired, countdown refresh, onboarding-gate mode, and the
    /// skipAuthQrGate bypass honored by the boot router (Task 18.2).
    /// </summary>
    public sealed class AuthQrSignInScreen : ScreenBase
    {
        private readonly QrLoginService _qrLogin = new QrLoginService(
            AppServices.Http, AppServices.Auth, AppServices.FileStore);

        private readonly Widgets.QrView _qrView = new Widgets.QrView();
        private readonly TextLabel _status = new TextLabel();
        private readonly TextLabel _countdown = new TextLabel();
        private CancellationTokenSource _pollCts;

        public bool OnboardingMode { get; set; }

        public AuthQrSignInScreen()
        {
            var heading = new TextLabel
            {
                Text = "Sign in with QR",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(0, 80),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Add(heading);

            _qrView.Position = new Position((1920 - 420) / 2f, 260);
            _qrView.Render("about:blank"); // placeholder frame until first startQr
            Add(_qrView);

            _status.Text = "Scan with the Nuvio mobile app";
            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(0, 720);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_status);

            _countdown.PointSize = DesignTokens.TypeSecondary;
            _countdown.TextColor = ThemeManager.Current.TextTertiaryColor;
            _countdown.Position = new Position(0, 780);
            _countdown.WidthResizePolicy = ResizePolicyType.FillToParent;
            _countdown.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _countdown.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_countdown);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            OnboardingMode = p?.GetBool("onboardingMode") ?? false;
            await StartQrAsync();
        }

        private async Task StartQrAsync()
        {
            StopPolling();
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;

            try
            {
                _status.Text = "Generating code…";
                var session = await _qrLogin.StartAsync(ct);
                if (session == null)
                {
                    _status.Text = "Could not start sign-in. Press OK to retry.";
                    return;
                }

                _qrView.Render(session.LoginUrl ?? session.Code);
                _status.Text = "Scan with the Nuvio mobile app";
                UpdateCountdown(session.ExpiresAtMillis);

                var result = await _qrLogin.PollUntilResolvedAsync(
                    session.Code,
                    session.DeviceNonce,
                    session.ExpiresAtMillis,
                    Math.Max(2, session.PollIntervalSeconds), ct);

                if (result == "approved")
                {
                    _status.Text = "Approved — signing in…";
                    if (_qrLogin.ExchangeAsync(session.Code).Result)
                    {
                        // Auth state flips via AuthManager; router reacts in Task 18.2.
                        _status.Text = "Signed in!";
                    }
                    else
                    {
                        _status.Text = "Exchange failed. Press OK to retry.";
                    }
                }
                else
                {
                    _status.Text = "Code expired.";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "qr flow failed: " + ex.Message);
                _status.Text = "Connection problem. Press OK to retry.";
            }
        }

        private void UpdateCountdown(long expiresAtMillis)
        {
            var remainingMs = expiresAtMillis - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _countdown.Text = remainingMs > 0
                ? $"Expires in {Math.Max(0, remainingMs / 1000 / 60)}:{Math.Max(0, remainingMs / 1000 % 60):00}"
                : "Expired";
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (key != NuvioKey.Ok) return false;
            _ = StartQrAsync(); // retry path (webapp handleRefreshAction parity)
            return true;
        }

        public override object ConsumeBackRequest() => null;

        private void StopPolling()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
        }

        public override void Cleanup() => StopPolling();
    }
}
