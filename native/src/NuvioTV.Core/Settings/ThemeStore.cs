using System;
using System.Collections.Generic;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Settings
{
    /// <summary>
    /// Port of js/data/local/themeStore.js: profile-scoped themeSettings with
    /// accent→theme reconciliation and settingsUiStyle validation.
    /// </summary>
    public static class ThemeStore
    {
        public const string StorageKey = "themeSettings";

        private static readonly Dictionary<string, string> ThemeByAccent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["#ffffff"] = "WHITE",
            ["#f5f5f5"] = "WHITE",
            ["#f5f8fc"] = "WHITE",
            ["#ff4d4f"] = "CRIMSON",
            ["#ff5252"] = "CRIMSON",
            ["#42a5f5"] = "OCEAN",
            ["#ba68c8"] = "VIOLET",
            ["#ab47bc"] = "VIOLET",
            ["#66bb6a"] = "EMERALD",
            ["#ffca28"] = "AMBER",
            ["#ffa726"] = "AMBER",
            ["#ec407a"] = "ROSE"
        };

        internal static readonly Dictionary<string, string> AccentByTheme = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WHITE"] = "#f5f5f5",
            ["CRIMSON"] = "#e53935",
            ["OCEAN"] = "#1e88e5",
            ["VIOLET"] = "#8e24aa",
            ["EMERALD"] = "#43a047",
            ["AMBER"] = "#fb8c00",
            ["ROSE"] = "#d81b60"
        };

        private static readonly ProfileScopedStore<ThemeSettings> Store =
            ProfileScopedStore<ThemeSettings>.Create(StorageKey, Normalize, ShallowMerge);

        /// <summary>Port of the JS default merge: {...current, ...partial}.</summary>
        public static ThemeSettings ShallowMerge(ThemeSettings current, ThemeSettings partial)
        {
            var baseValue = current ?? new ThemeSettings();
            var overlay = partial;
            if (overlay == null) return baseValue;
            return new ThemeSettings
            {
                Mode = overlay.Mode ?? baseValue.Mode,
                ThemeName = overlay.ThemeName ?? baseValue.ThemeName,
                AccentColor = overlay.AccentColor ?? baseValue.AccentColor,
                FontFamily = overlay.FontFamily ?? baseValue.FontFamily,
                AmoledMode = overlay.AmoledMode,
                AmoledSurfacesMode = overlay.AmoledSurfacesMode,
                SettingsUiStyle = overlay.SettingsUiStyle ?? baseValue.SettingsUiStyle
            };
        }

        /// <summary>Port of accentColorForTheme(): normalizes then maps to the canonical accent.</summary>
        public static string AccentColorForTheme(string themeName)
        {
            var normalized = UpperOr(themeName, ThemeDefaults.ThemeName);
            return AccentByTheme.TryGetValue(normalized, out var accent)
                ? accent
                : ThemeDefaults.AccentColor;
        }

        /// <summary>Port of normalizeTheme(): accent↔theme reconciliation + uiStyle whitelist.</summary>
        public static ThemeSettings Normalize(ThemeSettings settings)
        {
            var source = settings ?? new ThemeSettings();
            var accent = LowerOr(source.AccentColor, ThemeDefaults.AccentColor);
            var storedThemeName = UpperOr(source.ThemeName, ThemeDefaults.ThemeName);

            ThemeByAccent.TryGetValue(accent, out var themeFromAccent);

            var themeName = UpperOr(
                themeFromAccent != null && themeFromAccent != storedThemeName
                    ? themeFromAccent
                    : (!string.IsNullOrEmpty(storedThemeName) ? storedThemeName : themeFromAccent),
                ThemeDefaults.ThemeName);

            var normalizedAccent = AccentByTheme.ContainsKey(themeName)
                ? AccentColorForTheme(themeName)
                : LowerOr(accent, ThemeDefaults.AccentColor);

            return new ThemeSettings
            {
                Mode = source.Mode ?? ThemeDefaults.Mode,
                ThemeName = themeName,
                AccentColor = normalizedAccent,
                FontFamily = source.FontFamily ?? ThemeDefaults.FontFamily,
                Language = source.Language,
                AmoledMode = source.AmoledMode,
                AmoledSurfacesMode = source.AmoledSurfacesMode,
                SettingsUiStyle = IsValidUiStyle(source.SettingsUiStyle)
                    ? source.SettingsUiStyle.ToUpperInvariant()
                    : ThemeDefaults.SettingsUiStyle
            };
        }

        // ---- ProfileScopedStore passthroughs ----

        public static System.Threading.Tasks.Task<ThemeSettings> GetAsync(string profileId = null) =>
            profileId == null ? Store.GetAsync() : Store.GetAsync(profileId);

        public static System.Threading.Tasks.Task SetAsync(ThemeSettings partial, string profileId = null) =>
            profileId == null ? Store.SetAsync(partial) : Store.SetAsync(profileId, partial);
        public static System.Threading.Tasks.Task ReplaceAsync(ThemeSettings nextValue, string profileId = null) =>
            profileId == null ? Store.ReplaceAsync("1", nextValue) : Store.ReplaceAsync(profileId, nextValue);

        public static System.Threading.Tasks.Task SubscribeAsync(Action onChanged) => Store.SubscribeAsync(onChanged);

        // ---- helpers ----

        private static bool IsValidUiStyle(string value) =>
            value == "CLASSIC" || value == "HORIZON" || value == "ZEN" ||
            value == "classic" || value == "horizon" || value == "zen";

        private static string UpperOr(string value, string fallback) =>
            string.IsNullOrEmpty(value) ? fallback : value.ToUpperInvariant();

        private static string LowerOr(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var lowered = value.ToLowerInvariant();
            return lowered.Length == 0 ? fallback : lowered;
        }
    }

    internal static class ThemeDefaults
    {
        public const string Mode = "dark";
        public const string ThemeName = "WHITE";
        public const string AccentColor = "#ffffff";
        public const string FontFamily = "INTER";
        public const string SettingsUiStyle = "CLASSIC";
    }
}
