using System;
using System.Collections.Generic;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Modal dialog (components.css nuvio-dialog parity): dimmed backdrop,
    /// panel (default 1040px wide), vertical pill buttons. Enter animation
    /// fade 200ms + scale 0.92→1 over 280ms, exit ≈150ms. Ok activates the
    /// selected pill, Up/Down move selection with wrap-around. While open this
    /// acts as the modal gate: it consumes all keys.
    /// </summary>
    public sealed class NuvioDialog : View, IFocusable
    {
        private const int PanelWidth = 1040;
        private const int PanelHeight = 540;

        private readonly View _panel = new View();
        private readonly TextLabel _heading = new TextLabel();
        private readonly List<PillButton> _buttons = new List<PillButton>();
        private int _selectedIndex;

        public event Action<int> Activated;
        public event Action DismissRequested;

        public string FocusKey => "dialog:" + GetHashCode();

        public NuvioDialog(string heading)
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0f, 0f, 0f, 0.6f);

            _panel.Size = new Size(PanelWidth, PanelHeight);
            _panel.Position = new Position((1920 - PanelWidth) / 2f, (1080 - PanelHeight) / 2f);
            _panel.BackgroundColor = NuiFoundation.ThemeManager.Current.BgElevatedColor;
            _panel.CornerRadius = 16f;
            Add(_panel);

            _heading.Text = heading ?? "";
            _heading.PointSize = NuiFoundation.DesignTokens.TypeTitle;
            _heading.TextColor = NuiFoundation.ThemeManager.Current.TextColor;
            _heading.Position = new Position(48, 40);
            _heading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _heading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _panel.Add(_heading);
        }

        /// <summary>Adds a pill button row; returns its index.</summary>
        public int AddOption(string label)
        {
            var pill = new PillButton(label, _buttons.Count);
            pill.Activated += i =>
            {
                _selectedIndex = i;
                Activated?.Invoke(i);
            };
            pill.Position = new Position(48,
                140 + _buttons.Count * (NuiFoundation.DesignTokens.ButtonHeight + 18));
            _buttons.Add(pill);
            _panel.Add(pill);
            return _buttons.Count - 1;
        }

        public void Select(int index)
        {
            if (_buttons.Count == 0) return;
            _selectedIndex = Math.Max(0, Math.Min(index, _buttons.Count - 1));
            for (var i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].ApplyFocus(i == _selectedIndex);
            }
        }

        /// <summary>Handles a normalized key while open; true when consumed.</summary>
        public bool OnKey(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up:
                    Select(Move(_selectedIndex, _buttons.Count, -1));
                    return true;
                case NuvioKey.Down:
                    Select(Move(_selectedIndex, _buttons.Count, +1));
                    return true;
                case NuvioKey.Ok:
                    if (_buttons.Count > 0)
                    {
                        Activated?.Invoke(_selectedIndex);
                    }
                    return true;
                case NuvioKey.Back:
                    DismissRequested?.Invoke();
                    return true;
                default:
                    return true; // modal gate: swallow everything else
            }
        }

        public void ApplyFocus(bool focused) => Select(_selectedIndex);

        /// <summary>Enter transition then focus first option. Named Present to avoid View.Show.</summary>
        public void Present()
        {
            Select(_selectedIndex);
            try
            {
                Scale = new Vector3(0.92f, 0.92f, 1f);
                Opacity = 0f;
                var enter = new Animation(280);
                var ease = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseInOut);
                enter.AnimateTo(this, "Opacity", 1.0f, 0, 200);
                enter.AnimateTo(this, "ScaleX", 1f, ease);
                enter.AnimateTo(this, "ScaleY", 1f, ease);
                enter.Play();
            }
            catch
            {
                Opacity = 1f;
                Scale = new Vector3(1f, 1f, 1f);
            }
        }

        private static int Move(int index, int count, int delta) =>
            count <= 0 ? 0 : ((index + delta) % count + count) % count;

        private sealed class PillButton : View, IFocusable
        {
            private readonly TextLabel _label = new TextLabel();
            private readonly int _index;
            private static readonly Color NormalBg = new Color(0.133f, 0.133f, 0.133f, 1f);

            public event Action<int> Activated;

            public string FocusKey => "pill:" + _index;

            public PillButton(string text, int index)
            {
                _index = index;
                Size = new Size(944f, NuiFoundation.DesignTokens.ButtonHeight);
                CornerRadius = NuiFoundation.DesignTokens.ButtonHeight / 2f;
                BackgroundColor = NormalBg;

                _label.Text = text ?? "";
                _label.PointSize = NuiFoundation.DesignTokens.TypeBody;
                _label.TextColor = NuiFoundation.ThemeManager.Current.TextColor;
                _label.HorizontalAlignment = HorizontalAlignment.Center;
                _label.VerticalAlignment = VerticalAlignment.Center;
                _label.WidthResizePolicy = ResizePolicyType.FillToParent;
                _label.HeightResizePolicy = ResizePolicyType.FillToParent;
                Add(_label);
            }

            public void ApplyFocus(bool focused)
            {
                BackgroundColor = focused
                    ? NuiFoundation.ThemeManager.Current.FocusBgColor
                    : NormalBg;
            }
        }
    }
}
