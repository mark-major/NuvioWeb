using System;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Search input (searchField parity): NUI TextField styled as a rounded
    /// field; remote-key typing feeds characters via KeyString, backspace
    /// deletes, Ok submits. Clear button resets the query.
    /// </summary>
    public sealed class SearchField : View, IFocusable
    {
        private readonly TextField _field = new TextField();
        private readonly TextLabel _clearButton = new TextLabel();

        public event Action<string> QueryChanged;
        public event Action<string> Submitted;

        public string FocusKey => "search";

        public SearchField(string placeholder = "Search")
        {
            Size = new Size(1200, NuiFoundation.DesignTokens.ControlSize);
            CornerRadius = NuiFoundation.DesignTokens.ControlSize / 2f;
            BackgroundColor = NuiFoundation.ThemeManager.Current.CardBgColor;

            _field.PointSize = NuiFoundation.DesignTokens.TypeSubtitle;
            _field.TextColor = NuiFoundation.ThemeManager.Current.TextColor;
            _field.PlaceholderText = placeholder;
            _field.PlaceholderTextColor = NuiFoundation.ThemeManager.Current.TextTertiaryColor;
            _field.Position = new Position(40, 0);
            _field.Size = new Size(1060, NuiFoundation.DesignTokens.ControlSize);
            _field.VerticalAlignment = VerticalAlignment.Center;
            _field.TextChanged += (_, e) =>
            {
                if (e.TextField == _field)
                {
                    QueryChanged?.Invoke(_field.Text);
                }
            };
            Add(_field);

            _clearButton.Text = "×";
            _clearButton.PointSize = NuiFoundation.DesignTokens.TypeTitle;
            _clearButton.TextColor = NuiFoundation.ThemeManager.Current.TextSecondaryColor;
            _clearButton.HorizontalAlignment = HorizontalAlignment.Center;
            _clearButton.VerticalAlignment = VerticalAlignment.Center;
            _clearButton.Size = new Size(80, NuiFoundation.DesignTokens.ControlSize);
            _clearButton.Position = new Position(1120 - 80, 0);
            Add(_clearButton);
        }

        public string Text => _field.Text;

        public void SetText(string value)
        {
            _field.Text = value ?? "";
            QueryChanged?.Invoke(_field.Text);
        }

        public void Clear() => SetText("");

        /// <summary>
        /// Remote-key typing while the field holds focus: letters/digits insert,
        /// backspace deletes, Ok submits, left/right move the caret (native).
        /// Returns false for keys the field does not consume.
        /// </summary>
        public bool OnKey(NuvioKey key, char? character)
        {
            switch (key)
            {
                case NuvioKey.Back:
                    return false; // let router/screen close search
                case NuvioKey.Ok:
                    Submitted?.Invoke(_field.Text);
                    return true;
                default:
                    if (character.HasValue)
                    {
                        _field.Text += character.Value;
                        return true;
                    }
                    return false;
            }
        }

        public void ApplyFocus(bool focused)
        {
            BackgroundColor = focused
                ? NuiFoundation.ThemeManager.Current.FocusBgColor
                : NuiFoundation.ThemeManager.Current.CardBgColor;
        }
    }
}
