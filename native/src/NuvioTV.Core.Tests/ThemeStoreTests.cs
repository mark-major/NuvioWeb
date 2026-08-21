using System.Threading.Tasks;
using NuvioTV.Core.Settings;
using NuvioTV.Core.Storage;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the themeSettings port (js/data/local/themeStore.js).
    /// </summary>
    public class ThemeStoreTests
    {
        [Fact]
        public void Normalize_Defaults_MatchWebapp()
        {
            var normalized = ThemeStore.Normalize(new ThemeSettings());
            Assert.Equal("dark", normalized.Mode);
            Assert.Equal("WHITE", normalized.ThemeName);
            Assert.Equal("INTER", normalized.FontFamily);
            Assert.Null(normalized.Language);
            Assert.False(normalized.AmoledMode);
            Assert.False(normalized.AmoledSurfacesMode);
            // JS normalize maps the default #ffffff through ACCENT_BY_THEME → canonical "#f5f5f5".
            Assert.Equal("#f5f5f5", normalized.AccentColor);
        }

        [Theory]
        [InlineData("#ff4d4f", "CRIMSON")]
        [InlineData("#42a5f5", "OCEAN")]
        [InlineData("#ba68c8", "VIOLET")]
        [InlineData("#ab47bc", "VIOLET")]
        [InlineData("#ffca28", "AMBER")]
        [InlineData("#ffa726", "AMBER")]
        [InlineData("#66bb6a", "EMERALD")]
        [InlineData("#ec407a", "ROSE")]
        [InlineData("#f5f8fc", "WHITE")]
        public void Normalize_AccentOverridesStoredTheme(string accent, string expectedTheme)
        {
            // JS: when accent maps to a different theme than stored, accent wins.
            var normalized = ThemeStore.Normalize(new ThemeSettings
            {
                AccentColor = accent,
                ThemeName = "WHITE"
            });
            Assert.Equal(expectedTheme, normalized.ThemeName);
            Assert.Equal(ThemeStore.AccentColorForTheme(expectedTheme), normalized.AccentColor);
        }

        [Theory]
        [InlineData("classic", "CLASSIC")]
        [InlineData("horizon", "HORIZON")]
        [InlineData("zen", "ZEN")]
        [InlineData("bogus", "CLASSIC")]
        [InlineData(null, "CLASSIC")]
        public void Normalize_SettingsUiStyle_Whitelist(string input, string expected)
        {
            var normalized = ThemeStore.Normalize(new ThemeSettings { SettingsUiStyle = input });
            Assert.Equal(expected, normalized.SettingsUiStyle);
        }

        [Fact]
        public void Normalize_UnknownAccentKeepsValidStoredTheme()
        {
            var normalized = ThemeStore.Normalize(new ThemeSettings
            {
                AccentColor = "#123456",
                ThemeName = "ocean"
            });
            Assert.Equal("OCEAN", normalized.ThemeName);
            // Stored theme is valid and unmapped accent → accent canonicalizes from the theme.
            Assert.Equal(ThemeStore.AccentColorForTheme("OCEAN"), normalized.AccentColor);
        }

        [Theory]
        [InlineData("OCEAN", "#1e88e5")]
        [InlineData("VIOLET", "#8e24aa")]
        [InlineData("EMERALD", "#43a047")]
        [InlineData("AMBER", "#fb8c00")]
        [InlineData("ROSE", "#d81b60")]
        [InlineData("nonsense", "#ffffff")]
        public void AccentColorForTheme_MapsVerbatim(string themeName, string expected)
        {
            Assert.Equal(expected, ThemeStore.AccentColorForTheme(themeName));
        }

        [Fact]
        public async Task Store_PersistsPerProfile_AndNormalizes()
        {
            var store = new InMemoryKeyValueStore();
            var scoped = ProfileScopedStore<ThemeSettings>.CreateWithStore(
                "themeSettings:test", store,
                ThemeStore.Normalize,
                ThemeStore.ShallowMerge);

            await scoped.SetAsync("p1", new ThemeSettings { AccentColor = "#ff4d4f" });
            await scoped.SetAsync("p2", new ThemeSettings { AccentColor = "#42a5f5" });

            var p1 = await scoped.GetAsync("p1");
            var p2 = await scoped.GetAsync("p2");

            // Envelope shape parity: __profileScoped marker + version + profiles.
            var raw = await store.GetAsync("themeSettings:test");
            Assert.Contains("\"__profileScoped\":true", raw);
            Assert.Contains("\"version\":1", raw);
            Assert.Contains("\"profiles\"", raw);
        }
    }
}
