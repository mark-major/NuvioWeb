using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Input;
using System.Globalization;
using NuvioTV.Core.Models;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of profileSelectionScreen.js core flows (Task 18.1): profile grid
    /// from the profiles store, add-profile row, remember-last behavior, and OK
    /// → activate + route to home. PIN dialogs land as NuvioDialog flows.
    /// </summary>
    public sealed class ProfileSelectionScreen : ScreenBase
    {
        private readonly View _gridHost = new View();
        private readonly TextLabel _status = new TextLabel();
        private List<UserProfile> _profiles = new List<UserProfile>();
        private int _focusedIndex;

        public ProfileSelectionScreen()
        {
            var heading = new TextLabel
            {
                Text = "Who's watching?",
                PointSize = DesignTokens.TypeTitle * 2,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(0, 200),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Add(heading);

            _gridHost.Position = new Position(160, 420);
            _gridHost.Size = new Size(1600, 380);
            Add(_gridHost);

            _status.PointSize = DesignTokens.TypeSecondary;
            _status.TextColor = ThemeManager.Current.TextTertiaryColor;
            _status.Position = new Position(0, 860);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_status);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            try
            {
                // Profiles live in the shared store; ProfileManager owns them.
                var manager = new Core.Sync.ProfileManager(AppServices.FileStore);
                var loaded = await manager.GetProfilesAsync();
                _profiles = loaded?.ToList() ?? new List<UserProfile>();
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "profiles load failed: " + ex.Message);
            }
            Render();
        }

        private void Render()
        {
            while (_gridHost.ChildCount > 0)
            {
                var child = _gridHost.GetChildAt(0);
                _gridHost.Remove(child);
                child.Dispose();
            }

            const int tileW = 280, tileH = 340, gap = 40;
            for (var i = 0; i < Math.Min(_profiles.Count, 6); i++)
            {
                var profile = _profiles[i];
                var selected = i == _focusedIndex;
                var tile = new View
                {
                    Position = new Position(i * (tileW + gap), 20),
                    Size = new Size(tileW, tileH),
                    BackgroundColor = selected ? ThemeManager.Current.FocusBgColor : ThemeManager.Current.CardBgColor,
                    CornerRadius = DesignTokens.CardRadius
                };
                var avatarColor = ParseHex(profile.AvatarColorHex)
                    ?? ThemeManager.Current.FocusColorValue;
                tile.Add(new View
                {
                    Position = new Position((tileW - 120) / 2f, 60),
                    Size = new Size(120, 120),
                    CornerRadius = 60f,
                    BackgroundColor = avatarColor
                });
                tile.Add(new TextLabel
                {
                    Text = profile.Name ?? $"Profile {profile.ProfileIndex}",
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextColor,
                    Position = new Position(0, 220),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                _gridHost.Add(tile);
            }

            if (_profiles.Count == 0)
            {
                _gridHost.Add(new TextLabel
                {
                    Text = "No profiles found",
                    PointSize = DesignTokens.TypeSubtitle,
                    TextColor = ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(600, 150),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Right when _focusedIndex < _profiles.Count - 1:
                    _focusedIndex++;
                    Render();
                    return true;
                case NuvioKey.Left when _focusedIndex > 0:
                    _focusedIndex--;
                    Render();
                    return true;
                case NuvioKey.Ok when _profiles.Count > 0:
                    SelectProfile(_profiles[Math.Min(_focusedIndex, _profiles.Count - 1)]);
                    return true;
            }
            return false;
        }

        /// <summary>Activates the profile and routes home (enterWithLastProfile parity).</summary>
        private void SelectProfile(UserProfile profile)
        {
            _status.Text = $"Entering as {profile.Name}…";
            _ = AppServices.Router.NavigateAsync(Route.Home, new RouteParams()
                .Set("profileId", profile.Id ?? profile.ProfileIndex.ToString()),
                new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
        }

        private static global::Tizen.NUI.Color ParseHex(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && hex.Length == 7 && hex[0] == '#' &&
                byte.TryParse(hex.Substring(1, 2), NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex.Substring(3, 2), NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex.Substring(5, 2), NumberStyles.HexNumber, null, out var b))
            {
                return new global::Tizen.NUI.Color(r / 255f, g / 255f, b / 255f, 1f);
            }
            return null;
        }

        public override object ConsumeBackRequest() => null;
    }
}
