using System;
using NuvioTV.Core.Settings;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Port of js/ui/theme/themeManager.js apply(): resolves the palette for the
    /// active profile, applies AMOLED overrides, derives player aliases, and
    /// publishes the result as NuvioTheme.Current for all widgets.
    /// </summary>
    public static class ThemeManager
    {
        public static NuvioTheme Current { get; private set; } = new NuvioTheme();

        /// <summary>Maps the stored fontFamily key to a registered font family name.</summary>
        public static string ResolveFontFamily(string fontFamilyKey)
        {
            switch ((fontFamilyKey ?? "INTER").ToUpperInvariant())
            {
                case "DM_SANS": return "DM Sans";
                case "OPEN_SANS": return "Open Sans";
                default: return "Inter";
            }
        }

        /// <summary>Synchronously applies an already-loaded settings object.</summary>
        public static void Apply(ThemeSettings theme)
        {
            var effective = ThemeStore.Normalize(theme);
            var tokens = ThemeColors.GetPaletteTokens(effective.ThemeName);

            var resolved = new NuvioTheme
            {
                Bg = tokens[0],
                BgElevated = tokens[1],
                CardBg = tokens[2],
                Secondary = tokens[3],
                SecondaryVariant = tokens[4],
                OnSecondary = tokens[5],
                Text = tokens[6],
                TextSecondary = tokens[7],
                TextTertiary = tokens[8],
                Border = tokens[9],
                FocusColor = tokens[10],
                FocusBg = tokens[11],
                FontFamily = ResolveFontFamily(effective.FontFamily)
            };

            // AMOLED overrides (themeManager.js): bg → #000000; optional surfaces.
            if (effective.AmoledMode)
            {
                resolved.Bg = "#000000";
                if (effective.AmoledSurfacesMode)
                {
                    resolved.BgElevated = "#000000";
                    resolved.CardBg = "#000000";
                }
            }

            Current = resolved;
            global::Tizen.Log.Info("NuvioTV",
                $"theme applied: {resolved.FontFamily}, amoled={effective.AmoledMode}");
        }
    }
}
