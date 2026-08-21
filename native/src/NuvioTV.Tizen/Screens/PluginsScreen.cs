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
    /// Port of pluginsScreen.js: simple list of installed addons with enable /
    /// disable toggles (OK) and remove via long-press.
    /// </summary>
    public sealed class PluginsScreen : ScreenBase
    {
        private readonly View _listHost = new View();
        private readonly List<Core.Models.Addon> _addons = new List<Core.Models.Addon>();
        private int _focusedIndex;

        public PluginsScreen()
        {
            var heading = new TextLabel
            {
                Text = "Installed addons",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _listHost.Position = new Position(DesignTokens.SafeGutter, 160);
            _listHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 840);
            Add(_listHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var addons = await AppServices.Addons.GetInstalledAddonsAsync();
            _addons.Clear();
            _addons.AddRange(addons);
            _focusedIndex = Math.Min(_focusedIndex, Math.Max(0, _addons.Count - 1));
            Render();
        }

        private void Render()
        {
            while (_listHost.ChildCount > 0)
            {
                var child = _listHost.GetChildAt(0);
                _listHost.Remove(child);
                child.Dispose();
            }
            for (var i = 0; i < _addons.Count && i < 12; i++)
            {
                var addon = _addons[i];
                var row = new TextLabel
                {
                    Text = $"{(i == _focusedIndex ? "› " : "  ")}{addon.Name ?? addon.Id}   v{addon.Version}",
                    PointSize = DesignTokens.TypeBody,
                    TextColor = i == _focusedIndex
                        ? ThemeManager.Current.FocusColorValue
                        : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(0, i * (DesignTokens.ButtonHeight + 14)),
                    Size = new Size(1200, DesignTokens.ButtonHeight),
                    BackgroundColor = i == _focusedIndex
                        ? ThemeManager.Current.FocusBgColor
                        : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Begin,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _listHost.Add(row);
            }
        }

        public override async void Cleanup()
        {
            // Invalidate the cached assembly so Home sees reordered/removed sets.
            AppServices.Addons.InvalidateInstalledAddonsCache();
            await Task.CompletedTask;
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up when _focusedIndex > 0:
                    _focusedIndex--;
                    Render();
                    return true;
                case NuvioKey.Down when _focusedIndex < _addons.Count - 1:
                    _focusedIndex++;
                    Render();
                    return true;
                case NuvioKey.Ok when _addons.Count > 0:
                    ToggleEnabledAsync(_addons[_focusedIndex]);
                    return true;
            }
            return false;
        }

        private async void ToggleEnabledAsync(Core.Models.Addon addon)
        {
            var enabled = await AppServices.Addons.IsAddonEnabledAsync(addon.Id);
            await AppServices.Addons.SetEnabledAsync(addon.Id, !enabled);
            await MountAsync(new RouteParams(), null);
        }

        public override object ConsumeBackRequest() => null;
    }
}
