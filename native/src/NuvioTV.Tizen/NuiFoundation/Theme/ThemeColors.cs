using System;
using System.Collections.Generic;
using System.Globalization;
using Tizen.NUI;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>Resolved color set consumed by all widgets (js/ui/theme/themeManager.js apply()).</summary>
    public sealed class NuvioTheme
    {
        public string Bg = "#0d0d0d";
        public string BgElevated = "#1a1a1a";
        public string CardBg = "#222222";
        public string Secondary = "#f5f5f5";
        public string SecondaryVariant = "#e0e0e0";
        public string OnSecondary = "#111111";
        public string Text = "#ffffff";
        public string TextSecondary = "#b3b3b3";
        public string TextTertiary = "#808080";
        public string Border = "#333333";
        public string FocusColor = "#ffffff";
        public string FocusBg = "#303030";

        // Player aliases (derived in themeManager.js).
        public string PlayerSecondary => Secondary;
        public string PlayerOnSecondary => OnSecondary;
        public string PlayerFocusRing => FocusColor;
        public string PlayerFocusBackground => FocusBg;
        public string PlayerBackgroundElevated => BgElevated;
        public string PlayerBackgroundCard => CardBg;
        public string PlayerTextPrimary => Text;
        public string PlayerTextSecondary => TextSecondary;
        public string PlayerTextTertiary => TextTertiary;

        public Color BgColor => ThemeColors.ToColor(Bg);
        public Color BgElevatedColor => ThemeColors.ToColor(BgElevated);
        public Color CardBgColor => ThemeColors.ToColor(CardBg);
        public Color SecondaryColor => ThemeColors.ToColor(Secondary);
        public Color OnSecondaryColor => ThemeColors.ToColor(OnSecondary);
        public Color TextColor => ThemeColors.ToColor(Text);
        public Color TextSecondaryColor => ThemeColors.ToColor(TextSecondary);
        public Color TextTertiaryColor => ThemeColors.ToColor(TextTertiary);
        public Color BorderColor => ThemeColors.ToColor(Border);
        public Color FocusColorValue => ThemeColors.ToColor(FocusColor);
        public Color FocusBgColor => ThemeColors.ToColor(FocusBg);

        /// <summary>Font family name for TextLabel.FontFamily ("INTER"/"DM_SANS"/"OPEN_SANS").</summary>
        public string FontFamily { get; set; } = "Inter";
    }

    /// <summary>
    /// Port of js/ui/theme/themeColors.js: seven palettes × 12 tokens, values
    /// transcribed verbatim, plus AMOLED overrides applied by ThemeManager.
    /// </summary>
    public static class ThemeColors
    {
        public const int TokenCount = 12;

        private static readonly Dictionary<string, string[]> Palettes =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // token order: bg, bgElevated, cardBg, secondary, secondaryVariant,
                //              onSecondary, text, textSecondary, textTertiary, border,
                //              focusColor, focusBg
                ["WHITE"] = new[]
                {
                    "#0d0d0d", "#1a1a1a", "#222222", "#f5f5f5", "#e0e0e0",
                    "#111111", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#ffffff", "#303030"
                },
                ["CRIMSON"] = new[]
                {
                    "#0d0d0d", "#1a1a1a", "#241a1a", "#e53935", "#c62828",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#ff5252", "#3d1a1a"
                },
                ["OCEAN"] = new[]
                {
                    "#0d0d0f", "#1a1a1e", "#1a1f24", "#1e88e5", "#1565c0",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#42a5f5", "#1a2d3d"
                },
                ["VIOLET"] = new[]
                {
                    "#0d0d0f", "#1a1a1e", "#1f1a24", "#8e24aa", "#6a1b9a",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#ab47bc", "#2d1a3d"
                },
                ["EMERALD"] = new[]
                {
                    "#0d0d0d", "#1a1a1a", "#1a241a", "#43a047", "#2e7d32",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#66bb6a", "#1a3d1e"
                },
                ["AMBER"] = new[]
                {
                    "#0f0d0d", "#1e1a1a", "#24201a", "#fb8c00", "#ef6c00",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#ffa726", "#3d2d1a"
                },
                ["ROSE"] = new[]
                {
                    "#0d0d0d", "#1a1a1a", "#241a1f", "#d81b60", "#c2185b",
                    "#ffffff", "#ffffff", "#b3b3b3", "#808080", "#333333",
                    "#ec407a", "#3d1a2d"
                }
            };

        public static IReadOnlyList<string> PaletteNames =>
            new[] { "WHITE", "CRIMSON", "OCEAN", "VIOLET", "EMERALD", "AMBER", "ROSE" };

        /// <summary>Returns the raw 12-token array for a palette name (WHITE fallback).</summary>
        public static string[] GetPaletteTokens(string themeName)
        {
            var key = (themeName ?? "WHITE").ToUpperInvariant();
            return Palettes.TryGetValue(key, out var tokens) ? tokens : Palettes["WHITE"];
        }

        /// <summary>Parses #rrggbb into a Tizen.NUI.Color; white on parse failure.</summary>
        public static Color ToColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex) &&
                hex.Length == 7 && hex[0] == '#' &&
                byte.TryParse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return new Color(r / 255f, g / 255f, b / 255f, 1f);
            }
            return new Color(1f, 1f, 1f, 1f);
        }
    }
}
