using System;
using System.Collections.Generic;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Sidebar navigation (sidebarNavigation.js parity): legacy rail 144px /
    /// expanded 392px, vertical node list with focus helpers, and a 4-second
    /// auto-collapse timer while expanded.
    /// </summary>
    public sealed class SidebarNavigation : View, IFocusable
    {
        public const int RailWidth = 144;
        public const int ExpandedWidth = 392;

        /// <summary>One nav entry (node list port).</summary>
        public sealed class Item
        {
            public string Id;
            public string Label;

        }

        private sealed class RowView : View, IFocusable
        {
            public TextLabel Label = new TextLabel();
            public SidebarNavigation Owner;

            public string FocusKey => "nav:" + Index;
            public int Index;

            public void ApplyFocus(bool focused)
            {
                BackgroundColor = focused
                    ? NuiFoundation.ThemeManager.Current.FocusBgColor
                    : Color.Transparent;
                Label.TextColor = focused
                    ? NuiFoundation.ThemeManager.Current.TextColor
                    : NuiFoundation.ThemeManager.Current.TextSecondaryColor;
                if (focused)
                {
                    Owner?.NotifyRowFocused(Index);
                    Owner?.Expand();
                }
            }
        }

        private readonly List<Item> _items = new List<Item>();
        private readonly List<RowView> _rows = new List<RowView>();
        private readonly View _listHost = new View();
        private int _focusedIndex;
        private bool _expanded;
        private System.Threading.Timer _collapseTimer;

        public event Action<string> ItemActivated;

        public string FocusKey => "sidebar";

        public IReadOnlyList<Item> Items => _items;
        public int FocusedIndex => _focusedIndex;
        public bool IsExpanded => _expanded;

        public SidebarNavigation()
        {
            SizeWidth = RailWidth;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = NuiFoundation.ThemeManager.Current.BgElevatedColor;
            Add(_listHost);
        }

        public void SetItems(IEnumerable<Item> items)
        {
            foreach (var item in _items)
            {

            }
            _items.Clear();

            var index = 0;
            foreach (var item in items)
            {
                var row = new RowView
                {
                    Owner = this,
                    Index = index,
                    Size = new Size(ExpandedWidth - 32, NuiFoundation.DesignTokens.ButtonHeight),
                    Position = new Position(16, 24 + index * (NuiFoundation.DesignTokens.ButtonHeight + 12)),
                    CornerRadius = NuiFoundation.DesignTokens.ButtonHeight / 2f
                };
                row.Label.Text = item.Label ?? item.Id;
                row.Label.PointSize = NuiFoundation.DesignTokens.TypeBody;
                row.Label.HorizontalAlignment = HorizontalAlignment.Begin;
                row.Label.VerticalAlignment = VerticalAlignment.Center;
                row.Label.Position = new Position(24, 0);
                row.Label.WidthResizePolicy = ResizePolicyType.FillToParent;
                row.Label.HeightResizePolicy = ResizePolicyType.FillToParent;
                row.Add(row.Label);
                _listHost.Add(row);

                _rows.Add(row);
                _items.Add(item);
                index++;
            }
            ApplyWidth();
            FocusItem(0);
        }

        public void Expand()
        {
            if (_expanded) return;
            _expanded = true;
            ApplyWidth();
            RestartCollapseTimer();
        }

        public void Collapse()
        {
            _expanded = false;
            StopCollapseTimer();
            ApplyWidth();
        }

        /// <summary>Vertical focus movement with wrap-around; returns false at empty.</summary>
        public bool MoveFocus(int delta)
        {
            if (_items.Count == 0) return false;
            FocusItem(((_focusedIndex + delta) % _items.Count + _items.Count) % _items.Count);
            return true;
        }

        public void ActivateFocused()
        {
            if (_focusedIndex < 0 || _focusedIndex >= _items.Count) return;
            ItemActivated?.Invoke(_items[_focusedIndex].Id);
        }

        private void FocusItem(int index)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                _rows[i].ApplyFocus(i == index);
            }
            _focusedIndex = index;
        }

        private void NotifyRowFocused(int index) => _focusedIndex = index;

        private void ApplyWidth()
        {
            AnimateWidth(_expanded ? ExpandedWidth : RailWidth);
        }

        private void AnimateWidth(int target)
        {
            try
            {
                var animation = new Animation(200);
                animation.AnimateTo(this, "SizeWidth", (float)target);
                animation.Play();
            }
            catch
            {
                SizeWidth = target;
            }
        }

        private void RestartCollapseTimer()
        {
            StopCollapseTimer();
            _collapseTimer = new System.Threading.Timer(
                _ => Collapse(), null, NuiFoundation.DesignTokens.SidebarAutoCollapseMs, System.Threading.Timeout.Infinite);
        }

        private void StopCollapseTimer()
        {
            _collapseTimer?.Dispose();
            _collapseTimer = null;
        }

        private static class DesignTokenConstants
        {
            public const int SidebarAutoCollapseMs = NuiFoundation.DesignTokens.SidebarAutoCollapseMs;
        }

        public void ApplyFocus(bool focused)
        {
            BackgroundColor = focused
                ? NuiFoundation.ThemeManager.Current.BgElevatedColor
                : new Color(NuiFoundation.ThemeManager.Current.BgElevatedColor.R,
                    NuiFoundation.ThemeManager.Current.BgElevatedColor.G,
                    NuiFoundation.ThemeManager.Current.BgElevatedColor.B, 0.6f);
        }
    }
}
