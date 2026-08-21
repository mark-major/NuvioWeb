using System;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Account
{
    /// <summary>
    /// Port of syncCodeScreen.js: enter a TV-login code shown by the mobile/web
    /// client and exchange it for session tokens (tv-logins-exchange).
    /// </summary>
    public sealed class SyncCodeScreen : ScreenBase
    {
        private readonly Core.Auth.QrLoginService _qrLogin = new Core.Auth.QrLoginService(
            AppServices.Http, AppServices.Auth, AppServices.FileStore);

        private readonly TextLabel _codeLabel = new TextLabel();
        private readonly TextLabel _status = new TextLabel();
        private string _code = "";

        public SyncCodeScreen()
        {
            var heading = new TextLabel
            {
                Text = "Enter sync code",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(0, 160),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Add(heading);

            _codeLabel.PointSize = DesignTokens.TypeTitle * 2;
            _codeLabel.TextColor = ThemeManager.Current.FocusColorValue;
            _codeLabel.Position = new Position(0, 340);
            _codeLabel.WidthResizePolicy = ResizePolicyType.FillToParent;
            _codeLabel.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _codeLabel.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_codeLabel);

            var hint = new TextLabel
            {
                Text = "Type the code with letter keys; OK to submit",
                PointSize = DesignTokens.TypeSecondary,
                TextColor = ThemeManager.Current.TextTertiaryColor,
                Position = new Position(0, 520),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Add(hint);

            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(0, 620);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_status);
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _code = "";
            UpdateCode();
            return Task.CompletedTask;
        }

        private void UpdateCode() => _codeLabel.Text = _code.ToUpperInvariant();

        public override bool OnKeyDown(NuvioKey key)
        {
            char? c = null;
            switch (key)
            {
                case NuvioKey.LetterS: c = 's'; break;
                case NuvioKey.LetterT: c = 't'; break;
                case NuvioKey.LetterC: c = 'c'; break;
                case NuvioKey.LetterE: case NuvioKey.Stop: c = 'e'; break;
                case NuvioKey.LetterP: case NuvioKey.Pause: c = 'p'; break;
                case NuvioKey.LetterB: c = 'b'; break;
                case NuvioKey.LetterL: case NuvioKey.Left: c = 'l'; break;
            }

            if (c.HasValue && _code.Length < 12)
            {
                _code += c.Value;
                UpdateCode();
                return true;
            }
            if (key == NuvioKey.Back && _code.Length > 0)
            {
                _code = _code.Substring(0, _code.Length - 1);
                UpdateCode();
                return true;
            }
            if (key == NuvioKey.Ok && _code.Length >= 4)
            {
                SubmitAsync();
                return true;
            }
            return false;
        }

        private async void SubmitAsync()
        {
            try
            {
                _status.Text = "Exchanging code…";
                var ok = await _qrLogin.ExchangeAsync(_code);
                _status.Text = ok ? "Linked!" : "Invalid or expired code.";
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "sync code exchange failed: " + ex.Message);
                _status.Text = "Exchange failed.";
            }
        }
    }
}
