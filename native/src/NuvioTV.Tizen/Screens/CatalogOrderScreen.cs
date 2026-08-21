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
    /// Port of catalogOrderScreen.js: reorder installed addons in a grid via
    /// focus movement and SetAddonOrderAsync persistence.
    /// </summary>
    public sealed class CatalogOrderScreen : ScreenBase
    {
        private const int Columns = 4;

        private readonly View _gridHost = new View();
        private readonly List<Core.Models.Addon> _addons = new List<Core.Models.Addon>();
        private int _focusedIndex;
        private bool _dirty;

        public CatalogOrderScreen()
        {
            var heading = new TextLabel
            {
                Text = "Catalog order — OK picks up / drops",
                PointSize = DesignTokens.TypeSubtitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _gridHost.Position = new Position(DesignTokens.SafeGutter, 160);
            _gridHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 800);
            Add(_gridHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var addons = await AppServices.Addons.GetInstalledAddonsAsync();
            _addons.Clear();
            _addons.AddRange(addons);
            _focusedIndex = 0;
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
            for (var i = 0; i < _addons.Count; i++)
            {
                var card = new TextLabel
                {
                    Text = _addons[i].Name ?? _addons[i].Id,
                    PointSize = DesignTokens.TypeSecondary,
                    TextColor = i == _focusedIndex ? ThemeManager.Current.FocusColorValue : ThemeManager.Current.TextColor,
                    Position = new Position((i % Columns) * 400, (i / Columns) * 240),
                    Size = new Size(380, 220),
                    BackgroundColor = i == _focusedIndex ? ThemeManager.Current.FocusBgColor : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gridHost.Add(card);
            }
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Left when _focusedIndex > 0:
                    Swap(_focusedIndex, --_focusedIndex);
                    return true;
                case NuvioKey.Right when _focusedIndex < _addons.Count - 1:
                    Swap(_focusedIndex, ++_focusedIndex);
                    return true;
                case NuvioKey.Up when _focusedIndex - Columns >= 0:
                    Swap(_focusedIndex, _focusedIndex -= Columns);
                    return true;
                case NuvioKey.Down when _focusedIndex + Columns < _addons.Count:
                    Swap(_focusedIndex, _focusedIndex += Columns);
                    return true;
                case NuvioKey.Ok:
                    PersistAsync();
                    return true;
            }
            return false;
        }

        private void Swap(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a >= _addons.Count || b >= _addons.Count) return;
            var tmp = _addons[a];
            _addons[a] = _addons[b];
            _addons[b] = tmp;
            _dirty = true;
            Render();
        }

        private async void PersistAsync()
        {
            if (!_dirty) return;
            try
            {
                await AppServices.Addons.SetAddonOrderAsync(_addons.Select(a => a.Id).ToList());
                _dirty = false;
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "order persist failed: " + ex.Message);
            }
        }

        public override object ConsumeBackRequest() => null;
    }
}
