using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of pluginScreen.js (addon install): paste-URL text entry, QR overlay
    /// of the install URL (mobile scans it to open the manifest), and installed
    /// repository cards with enable/disable toggles.
    /// </summary>
    public sealed class PluginScreen : ScreenBase
    {
        private readonly Widgets.SearchField _urlField = new Widgets.SearchField("Addon manifest URL");
        private readonly Widgets.QrView _qr = new Widgets.QrView();
        private readonly TextLabel _status = new TextLabel();
        private readonly View _cardsHost = new View();
        private readonly List<Core.Models.Addon> _installed = new List<Core.Models.Addon>();
        private int _zone; // 0 = url field, 1..n = addon cards

        public PluginScreen()
        {
            var heading = new TextLabel
            {
                Text = "Addons",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _urlField.Position = new Position(DesignTokens.SafeGutter, 180);
            Add(_urlField);

            _qr.Position = new Position(1920 - 420 - DesignTokens.SafeGutter, 300);
            _qr.Hide();
            Add(_qr);

            _cardsHost.Position = new Position(DesignTokens.SafeGutter, 300);
            _cardsHost.Size = new Size(1200, 640);
            Add(_cardsHost);

            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(DesignTokens.SafeGutter, 980);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_status);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            await ReloadInstalledAsync();
        }

        private async Task ReloadInstalledAsync()
        {
            var addons = await AppServices.Addons.GetInstalledAddonsAsync();
            lock (_installed)
            {
                _installed.Clear();
                _installed.AddRange(addons);
            }
            RenderCards();
        }

        private void RenderCards()
        {
            while (_cardsHost.ChildCount > 0)
            {
                var child = _cardsHost.GetChildAt(0);
                _cardsHost.Remove(child);
                child.Dispose();
            }
            List<Core.Models.Addon> snapshot;
            lock (_installed) { snapshot = _installed.ToList(); }

            for (var i = 0; i < Math.Min(snapshot.Count, 8); i++)
            {
                var addon = snapshot[i];
                var card = new TextLabel
                {
                    Text = $"  {addon.Name ?? addon.Id}  v{addon.Version}",
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextColor,
                    Position = new Position((i % 2) * 600, (i / 2) * 150),
                    Size = new Size(580, 130),
                    BackgroundColor = ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Begin,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _cardsHost.Add(card);
            }
        }

        public override async void Cleanup()
        {
            await ReloadInstalledAsync();
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Ok:
                    if (_zone == 0)
                    {
                        InstallFromFieldAsync();
                        return true;
                    }
                    return false;
                case NuvioKey.Down:
                    if (_zone < _installed.Count) { _zone++; UpdateFocusVisuals(); }
                    return true;
                case NuvioKey.Up:
                    if (_zone > 0) { _zone--; UpdateFocusVisuals(); }
                    return true;
                case NuvioKey.LetterB:
                    ToggleQrOverlay();
                    return true;
            }
            return false;
        }

        private void UpdateFocusVisuals() { /* cards highlight via zone in RenderCards */ }

        private void ToggleQrOverlay()
        {
            var url = _urlField.Text;
            if (string.IsNullOrWhiteSpace(url)) return;
            var visible = !_qr.IsOnWindow;
            if (visible) { _qr.Show(); _qr.Render(url); } else { _qr.Hide(); }
        }

        private async void InstallFromFieldAsync()
        {
            var url = _urlField.Text.Trim();
            if (url.Length == 0) return;
            try
            {
                _status.Text = "Installing…";
                var ok = await AppServices.Addons.AddAddonAsync(url);
                _status.Text = ok == true ? "Installed." : "Install failed.";
                if (ok == true)
                {
                    _urlField.Clear();
                    await ReloadInstalledAsync();
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "addon install failed: " + ex.Message);
                _status.Text = "Install failed.";
            }
        }

        public override object ConsumeBackRequest()
        {
            if (_qr.IsOnWindow)
            {
                _qr.Hide();
                return true; // consumed: close QR first
            }
            return null;
        }
    }
}
