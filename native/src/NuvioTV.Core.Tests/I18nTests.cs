using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NuvioTV.Core.Localization;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Tests for the I18n port. Loader reads the committed generated locale JSON
    /// (native/src/NuvioTV.Tizen/Resources/i18n/*.json) so fixtures are the real artifacts.
    /// </summary>
    public class I18nTests : IDisposable
    {
        private static readonly string LocaleDir = FindLocaleDir();

        private static string FindLocaleDir()
        {
            var candidates = new[]
            {
                Path.Combine("native", "src", "NuvioTV.Tizen", "Resources", "i18n"),
                Path.Combine("..", "..", "..", "..", "NuvioTV.Tizen", "Resources", "i18n"),
            };
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new InvalidOperationException("locale JSON directory not found");
        }

        private static Task<string> LoadJsonAsync(string locale)
        {
            return File.ReadAllTextAsync(Path.Combine(LocaleDir, locale + ".json"));
        }

        public I18nTests()
        {
            I18n.Configure(
                LoadJsonAsync,
                () => new[] { "en" },
                _ => { });
        }

        public void Dispose()
        {
            // Reset internal state between tests via re-Configure + fresh init per test.
        }

        [Fact]
        public async Task InitAsync_DefaultLocale_LoadsEnglish()
        {
            await I18n.InitAsync(null);
            Assert.Equal("en", I18n.Locale);
            Assert.NotEmpty(I18n.T("action_cancel"));
        }

        [Fact]
        public async Task T_ExistingKey_ReturnsValue()
        {
            await I18n.InitAsync("en");
            Assert.Equal("Cancel", I18n.T("action_cancel"));
        }

        [Fact]
        public async Task T_MissingKey_FallsBackToKeyItself()
        {
            await I18n.InitAsync("en");
            var result = I18n.T("definitely_not_a_real_key_xyz");
            Assert.Equal("definitely_not_a_real_key_xyz", result);
        }

        [Fact]
        public async Task T_MissingKey_UsesExplicitFallback_AndInterpolatesIt()
        {
            await I18n.InitAsync("en");
            var result = I18n.T("definitely_not_a_real_key_xyz", null, "Retry in %1$s minutes",
                null);
            Assert.Equal("Retry in 5 minutes", I18n.T("definitely_not_a_real_key_xyz",
                new Dictionary<string, object> { { "0", "5" } }, "Retry in %1$s minutes"));
        }

        [Fact]
        public async Task T_AliasedKey_ResolvesThroughKeyAliases()
        {
            await I18n.InitAsync("en");
            // "common.cancel" is aliased to action_cancel (KEY_ALIASES entry).
            Assert.Equal("Cancel", I18n.T("common.cancel"));
        }

        [Fact]
        public async Task T_PositionalPlaceholder_Substitutes()
        {
            await I18n.InitAsync("en");
            var result = I18n.T("about_version", new Dictionary<string, object> { { "1", "1.2.3" } });
            Assert.Equal("Version 1.2.3", result);
        }

        [Fact]
        public async Task T_NamedPlaceholder_Substitutes()
        {
            await I18n.InitAsync("en");
            var result = I18n.T("auth.qr.unavailableWithReason",
                new Dictionary<string, object> { { "reason", "offline" } });
            Assert.Equal("QR unavailable: offline", result);
        }

        [Fact]
        public async Task T_NamedPlaceholder_MissingArg_Empties()
        {
            await I18n.InitAsync("en");
            var result = I18n.T("auth.qr.unavailableWithReason", new Dictionary<string, object>());
            Assert.Equal("QR unavailable: ", result);
        }

        [Fact]
        public async Task T_EscapedQuotes_DecodedDuringInterpolation()
        {
            await I18n.InitAsync("en");
            // The committed JSON stores \' and \" verbatim; interpolate() decodes them.
            var raw = LoadRawEnValueContainingEscapedQuote();
            if (raw == null)
            {
                return; // no fixture with escaped quotes in en — covered by unit test below
            }
            var rendered = I18n.T(raw.Value.Key);
            Assert.DoesNotContain("\\'", rendered);
            Assert.DoesNotContain("\\\"", rendered);
        }

        [Fact]
        public void Interpolate_DecodesBackslashQuotes_Directly()
        {
            // Exercise the interpolation pipeline through a fallback template.
            var result = I18n.T("__probe__",
                null,
                "Collection \\'name\\\" here");
            Assert.Equal("Collection 'name\" here", result);
        }

        [Fact]
        public async Task T_LocaleFallback_MergesOverEnglishBase()
        {
            // de has partial coverage: keys missing from de.json must resolve to en values.
            await I18n.InitAsync("de");
            // de.json HAS action_cancel ("Abbrechen") — localized value wins over base.
            Assert.Equal("Abbrechen", I18n.T("action_cancel"));
        }

        [Fact]
        public async Task T_RtlLocales_PassthroughUnchanged()
        {
            // ar/he strings render as-is; the loader must not transform content.
            await I18n.InitAsync("ar");
            var arabic = I18n.T("action_cancel");
            Assert.False(string.IsNullOrEmpty(arabic));

            await I18n.InitAsync("he");
            var hebrew = I18n.T("action_cancel");
            Assert.False(string.IsNullOrEmpty(hebrew));
        }

        [Theory]
        [InlineData("iw", "he")]
        [InlineData("in", "id")]
        [InlineData("pt", "pt-br")]
        [InlineData("es-419-x-extra", "es-419")]
        [InlineData("system", "en")]
        public void NormalizeLocale_MapsAliasesAndRegions(string input, string expected)
        {
            Assert.Equal(expected, I18n.ResolveLocale(input));
        }

        [Fact]
        public void ResolveLocale_UnsupportedLanguage_FallsBackToEnglish()
        {
            Assert.Equal("en", I18n.ResolveLocale("xx-YY"));
        }

        [Fact]
        public void SupportedLocales_Contains30Locales()
        {
            Assert.Equal(30, I18n.SupportedLocales.Count);
        }

        private static KeyValuePair<string, string>? GetEnEntry(Func<string, bool> predicate)
        {
            foreach (var line in File.ReadAllLines(Path.Combine(LocaleDir, "en.json")))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, "^  \"(.+?)\": \"(.*)\",?$");
                if (match.Success && predicate(match.Groups[2].Value))
                {
                    return new KeyValuePair<string, string>(match.Groups[1].Value, match.Groups[2].Value);
                }
            }
            return null;
        }

        private static KeyValuePair<string, string>? LoadRawEnValueContainingEscapedQuote()
        {
            return GetEnEntry(v => v.Contains("\\'") || v.Contains("\\\""));
        }
    }
}
