using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NuvioTV.Core.Streams;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the stream presentation/selection logic ports:
    /// releaseToken.js, streamDisplayText.js, streamResumeIdentity.js,
    /// streamAutoPlaySelector.js, debridStreamTemplateEngine.js, streamBadgeRules.js.
    /// </summary>
    public class StreamLogicTests
    {
        private static JsonElement Json(string json)
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }

        // ------------------------------------------------------------------
        // ReleaseToken (releaseToken.js)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Movie 2160p WEB-DL", "2160p", true)]
        [InlineData("movie.2160p.web", "2160P", true)] // case-insensitive
        [InlineData("not2160pmatch", "2160p", false)] // word boundary required
        [InlineData("2160p at start", "2160p", true)]
        [InlineData("ends with 2160p", "2160p", true)]
        [InlineData("", "2160p", false)]
        [InlineData("anything", "", false)]
        public void HasReleaseToken_WordBoundaryMatching(string text, string token, bool expected)
        {
            Assert.Equal(expected, ReleaseToken.HasReleaseToken(text, token));
        }

        [Fact]
        public void HasReleaseToken_EscapesRegexMetacharacters()
        {
            Assert.True(ReleaseToken.HasReleaseToken("title (2020) rip", "(2020)"));
            Assert.False(ReleaseToken.HasReleaseToken("title 2020 rip", "(2020)"));
        }

        // ------------------------------------------------------------------
        // StreamDisplayText (streamDisplayText.js)
        // ------------------------------------------------------------------

        [Fact]
        public void MathematicalSymbols_NormalizeToAscii()
        {
            // U+1D7D9 is MATHEMATICAL DOUBLE-STRUCK DIGIT... use bold "4" U+1D7CE region:
            var styled = "𝟒𝐊"; // mathematical bold digit four + bold K
            var normalized = StreamDisplayText.NormalizeMathematicalAlphanumericSymbols(styled);
            Assert.Equal("4K", normalized);
        }

        [Fact]
        public void MathematicalSymbols_PassThroughPlainAndEmoji()
        {
            Assert.Equal("4K HDR 😀", StreamDisplayText.NormalizeMathematicalAlphanumericSymbols("4K HDR 😀"));
        }

        // ------------------------------------------------------------------
        // StreamResumeIdentity (streamResumeIdentity.js)
        // ------------------------------------------------------------------

        [Fact]
        public void ResumeIdentity_TorrentPath_PrefersInfoHash()
        {
            var stream = Json(@"{
                ""addonId"": ""Addon A"",
                ""infoHash"": ""ABC123"",
                ""fileIdx"": 2,
                ""clientResolve"": { ""service"": ""RealDebrid"" },
                ""behaviorHints"": { ""filename"": ""Some.Show.S01E02.mkv"" }
            }");

            var identity = StreamResumeIdentity.BuildStreamResumeIdentity(stream);

            Assert.StartsWith("torrent:", identity);
            Assert.Contains("\"abc123\"", identity);
            Assert.Contains("\"addon a\"", identity);
            Assert.Contains("\"realdebrid\"", identity);
            Assert.Contains("\"2\"", identity);
        }

        [Fact]
        public void ResumeIdentity_FilePath_WhenOnlyFilename()
        {
            var stream = Json(@"{
                ""behaviorHints"": { ""filename"": ""Movie.mkv"" },
                ""url"": ""https://x.io/f.mkv""
            }");

            var identity = StreamResumeIdentity.BuildStreamResumeIdentity(stream);

            Assert.StartsWith("file:", identity);
            Assert.Contains("\"movie.mkv\"", identity);
        }

        [Theory]
        [InlineData("magnet:?xt=urn:btih:ABC", "magnet")]
        [InlineData("https://x.io/file.mkv", "url")]
        public void ResumeIdentity_LocatorKind(string locator, string expectedKind)
        {
            var stream = Json("{ \"url\": \"" + locator + "\" }");
            var identity = StreamResumeIdentity.BuildStreamResumeIdentity(stream);
            Assert.StartsWith(expectedKind + ":", identity);
        }

        [Fact]
        public void ResumeIdentity_EmptyStream_ReturnsEmpty()
        {
            Assert.Equal("", StreamResumeIdentity.BuildStreamResumeIdentity(Json("{}")));
        }

        // ------------------------------------------------------------------
        // StreamAutoPlaySelector (streamAutoPlaySelector.js)
        // ------------------------------------------------------------------

        private static JsonElement Stream(string name, string url, string addonName = "Cinemeta",
            string bingeGroup = null, string debridState = null)
        {
            var bg = bingeGroup != null ? ", \"behaviorHints\": { \"bingeGroup\": \"" + bingeGroup + "\" }" : "";
            var ds = debridState != null ? ", \"debridCacheStatus\": { \"state\": \"" + debridState + "\" }" : "";
            return Json("{\"name\": \"" + name + "\", \"url\": \"" + url +
                        "\", \"addonName\": \"" + addonName + "\"" + bg + ds + "}");
        }

        [Fact]
        public void AutoPlay_ManualMode_ReturnsNullWithoutBingePreference()
        {
            var streams = new[] { Stream("A", "https://a") };
            Assert.Null(StreamAutoPlaySelector.SelectAutoPlayStream(streams, mode: "MANUAL"));
        }

        [Fact]
        public void AutoPlay_FirstStream_SkipsUnplayableDebridStates()
        {
            var streams = new[]
            {
                Stream("Checking", "https://a", debridState: "CHECKING"),
                Stream("NotCached", "https://b", debridState: "NOT_CACHED"),
                Stream("Good", "https://c")
            };
            var selected = StreamAutoPlaySelector.SelectAutoPlayStream(streams, mode: "FIRST_STREAM");
            Assert.Equal("Good", selected.Value.Str("name"));
        }

        [Fact]
        public void AutoPlay_RegexMatch_IncludesAndExcludes()
        {
            var streams = new[]
            {
                Stream("CAM Rip HD", "https://a"),
                Stream("WEB-DL 1080p", "https://b")
            };

            // Pattern includes 1080p but excludes CAM via negative lookahead — Android parity.
            var selected = StreamAutoPlaySelector.SelectAutoPlayStream(
                streams,
                mode: "REGEX_MATCH",
                regexPattern: "(?!.*(CAM|TS)).*1080p");

            Assert.NotNull(selected);
            Assert.Equal("WEB-DL 1080p", selected.Value.Str("name"));

            var none = StreamAutoPlaySelector.SelectAutoPlayStream(
                new[] { Stream("CAM 1080p", "https://a") },
                mode: "REGEX_MATCH",
                regexPattern: "(?!.*(CAM|TS)).*1080p");
            Assert.Null(none);
        }

        [Fact]
        public void AutoPlay_BingeGroupPriority_AppliesEvenInManualMode()
        {
            var streams = new[]
            {
                Stream("First", "https://a"),
                Stream("Binge", "https://b", bingeGroup: "tt1:2")
            };
            var selected = StreamAutoPlaySelector.SelectAutoPlayStream(
                streams,
                mode: "MANUAL",
                preferredBingeGroup: "tt1:2",
                preferBingeGroupInSelection: true);

            Assert.NotNull(selected);
            Assert.Equal("Binge", selected.Value.Str("name"));

            // bingeGroupOnly: a miss must open the picker.
            var miss = StreamAutoPlaySelector.SelectAutoPlayStream(
                new[] { Stream("First", "https://a") },
                mode: "MANUAL",
                preferredBingeGroup: "tt9:9",
                preferBingeGroupInSelection: true,
                bingeGroupOnly: true);
            Assert.Null(miss);
        }

        [Fact]
        public void AutoPlay_SourceScoping_FiltersByInstalledAddons()
        {
            var streams = new[]
            {
                Stream("Plugin", "https://a", addonName: "Torrentio"),
                Stream("Addon", "https://b", addonName: "Cinemeta")
            };

            var installedOnly = StreamAutoPlaySelector.SelectAutoPlayStream(
                streams, mode: "FIRST_STREAM", source: "INSTALLED_ADDONS_ONLY",
                installedAddonNames: new[] { "Cinemeta" });
            Assert.Equal("Addon", installedOnly.Value.Str("name"));

            var pluginsOnly = StreamAutoPlaySelector.SelectAutoPlayStream(
                streams, mode: "FIRST_STREAM", source: "ENABLED_PLUGINS_ONLY",
                installedAddonNames: new[] { "Cinemeta" });
            Assert.Equal("Plugin", pluginsOnly.Value.Str("name"));
        }

        [Fact]
        public void AutoPlay_InvalidRegex_ReturnsNull()
        {
            var streams = new[] { Stream("A", "https://a") };
            Assert.Null(StreamAutoPlaySelector.SelectAutoPlayStream(
                streams, mode: "REGEX_MATCH", regexPattern: "([unclosed"));
        }

        [Fact]
        public void IsRegexSelectionConfigured_RequiresAlphanumeric()
        {
            Assert.False(StreamAutoPlaySelector.IsRegexSelectionConfigured(""));
            Assert.False(StreamAutoPlaySelector.IsRegexSelectionConfigured(".*+?"));
            Assert.True(StreamAutoPlaySelector.IsRegexSelectionConfigured("1080p"));
        }

        // ------------------------------------------------------------------
        // DebridStreamTemplateEngine (debridStreamTemplateEngine.js)
        // ------------------------------------------------------------------

        // JS parity: the engine looks up FULL dotted keys (values["stream.name"]),
        // so callers pass a flat map.
        private static Dictionary<string, object> TemplateValues()
        {
            return new Dictionary<string, object>
            {
                ["stream.name"] = "Big Buck Bunny",
                ["stream.quality"] = "1080p",
                ["stream.size"] = 2500000000.0,
                ["stream.duration"] = 5460.0,
                ["stream.tags"] = new object[] { "HDR", "DV" },
                ["service.id"] = "realdebrid",
                ["service.cached"] = true
            };
        }

        [Fact]
        public void Template_PlainPlaceholder_AndTransforms()
        {
            var values = TemplateValues();
            Assert.Equal("Big Buck Bunny", DebridStreamTemplateEngine.Render("{stream.name}", values));
            Assert.Equal("big buck bunny", DebridStreamTemplateEngine.Render("{stream.name::lower}", values));
            Assert.Equal("BIG BUCK BUNNY", DebridStreamTemplateEngine.Render("{stream.name::upper}", values));
            Assert.Equal("Big Buck Bunny", DebridStreamTemplateEngine.Render("{stream.name::title}", values));
        }

        [Fact]
        public void Template_BytesAndTime_Formats()
        {
            var values = TemplateValues();
            Assert.Equal("2.3 GB", DebridStreamTemplateEngine.Render("{stream.size::bytes}", values));
            Assert.Equal("1h 31m", DebridStreamTemplateEngine.Render("{stream.duration::time}", values));
        }

        [Fact]
        public void Template_JoinTransform()
        {
            var values = TemplateValues();
            // Separators must be quoted (legacy template uses join(\' | \')).
            Assert.Equal("HDR, DV", DebridStreamTemplateEngine.Render("{stream.tags::join(', ')}", values));
            Assert.Equal("HDR/DV", DebridStreamTemplateEngine.Render("{stream.tags::join('/')}", values));
        }

        [Fact]
        public void Template_ConditionBranches()
        {
            var values = TemplateValues();
            // Real DSL: {field::ops["then"||"else"]} — no ternary operator.
            Assert.Equal("RD cached", DebridStreamTemplateEngine.Render(
                "{service.cached::istrue[\"RD cached\"||\"not cached\"]}", values));
            // JS parity: isfalse tests the value IS boolean false (no negation of
            // prior results) — with cached=true it takes the else branch.
            Assert.Equal("RD cached", DebridStreamTemplateEngine.Render(
                "{service.cached::isfalse[\"not cached\"||\"RD cached\"]}", values));
            var uncached = new Dictionary<string, object>(TemplateValues());
            uncached["service.cached"] = false;
            Assert.Equal("not cached", DebridStreamTemplateEngine.Render(
                "{service.cached::isfalse[\"not cached\"||\"RD cached\"]}", uncached));
        }

        [Fact]
        public void Template_ConditionOperators_AndOr()
        {
            var values = TemplateValues();
            Assert.Equal("yes", DebridStreamTemplateEngine.Render(
                "{stream.quality::~=1080::and::stream.quality::~=p[\"yes\"||\"no\"]}", values));
            Assert.Equal("no", DebridStreamTemplateEngine.Render(
                "{stream.quality::~=720::or::stream.quality::~=2160[\"yes\"||\"no\"]}", values));
            Assert.Equal("yes", DebridStreamTemplateEngine.Render(
                "{stream.quality::=1080p[\"yes\"||\"no\"]}", values));
        }

        [Fact]
        public void Template_NestedRender_InBranches()
        {
            var values = TemplateValues();
            var output = DebridStreamTemplateEngine.Render(
                "{service.cached::istrue[\"{stream.quality} RD\"||\"plain\"]}", values);
            Assert.Equal("1080p RD", output);
        }

        [Fact]
        public void Template_UnclosedPlaceholder_PassesThrough()
        {
            var values = TemplateValues();
            Assert.Equal("{stream.name", DebridStreamTemplateEngine.Render("{stream.name", values));
        }

        [Fact]
        public void Template_ReplaceTransform()
        {
            var values = TemplateValues();
            Assert.Equal("Big-Buck-Bunny", DebridStreamTemplateEngine.Render(
                "{stream.name::replace(' ', '-')}", values));
        }

        // ------------------------------------------------------------------
        // StreamBadgeRules (streamBadgeRules.js)
        // ------------------------------------------------------------------

        private static NormalizedBadgeRules Rules(string json)
        {
            return StreamBadgeRules.NormalizeStreamBadgeRules(Json(json));
        }

        [Fact]
        public void BadgeRules_NormalizesColors_AndDedupesImports()
        {
            var rules = Rules(@"{
                ""imports"": [
                    { ""sourceUrl"": ""http://a"", ""filters"": [
                        { ""name"": ""HDR"", ""pattern"": ""HDR"", ""tagColor"": ""#aabbccdd"" }
                    ]},
                    { ""sourceUrl"": ""http://A/"", ""filters"": [
                        { ""name"": ""X"", ""pattern"": ""x"" }
                    ]},
                    { ""sourceUrl"": ""http://b"", ""filters"": [
                        { ""name"": ""Y"", ""pattern"": ""y"" }
                    ]},
                    { ""sourceUrl"": ""http://c"", ""filters"": [
                        { ""name"": ""Z"", ""pattern"": ""z"" }
                    ]}
                ]
            }");

            // normalizeText keeps trailing slashes, so http://A/ ≠ http://a — no dedupe.
            // Import cap of 3 keeps the first three; first becomes active by default.
            Assert.Equal(3, rules.Imports.Count);
            Assert.Equal("HDR", rules.Imports[0].Filters[0].Name);
            Assert.Equal("X", rules.Imports[1].Filters[0].Name);
            Assert.True(rules.Imports[0].IsActive);
            Assert.False(rules.Imports[1].IsActive);
            // #aabbccdd alpha < 255 → rgba form.
            Assert.Equal("rgba(187, 204, 221, 0.667)", rules.Imports[0].Filters[0].TagColor);
        }

        [Fact]
        public void BadgeRules_MatchesAcrossCandidateFields_AndDedupes()
        {
            var rules = Rules(@"{
                ""imports"": [{ ""sourceUrl"": ""http://a"", ""filters"": [
                    { ""name"": ""HDR10"", ""pattern"": ""HDR10|HDR"" },
                    { ""name"": ""DV"", ""pattern"": ""Dolby.?Vision|\\\\bDV\\\\b"" }
                ]}]
            }");

            var stream = Json(@"{
                ""name"": ""Movie 2160p WEB-DL"",
                ""description"": ""High quality HDR release""
            }");

            var badges = StreamBadgeRules.MatchStreamBadges(stream, rules);

            Assert.Single(badges);
            Assert.Equal("HDR10", badges[0].Name);
        }

        [Fact]
        public void BadgeRules_MergeKeepsExistingPriority()
        {
            var existing = new[] { new StreamBadge("Existing", "img-existing", null, null, null, null) };
            var matched = new[]
            {
                new StreamBadge("Existing", "img-existing", null, null, null, null),
                new StreamBadge("New", "img-new", null, null, null, null)
            };

            var merged = StreamBadgeRules.MergeStreamBadges(existing, matched);

            Assert.Equal(2, merged.Count);
            Assert.Equal("Existing", merged[0].Name);
            Assert.Equal("New", merged[1].Name);
        }

        [Fact]
        public void BadgeRules_InlineFlags_StrippedFromPattern()
        {
            var rules = Rules(@"{
                ""imports"": [{ ""sourceUrl"": ""http://a"", ""filters"": [
                    { ""name"": ""Caseless"", ""pattern"": ""(?i)hdr10+"" }
                ]}]
            }");

            var stream = Json(@"{ ""name"": ""MOVIE HDR10PLUS"" }");
            var badges = StreamBadgeRules.MatchStreamBadges(stream, rules);

            Assert.Single(badges);
            Assert.Equal("Caseless", badges[0].Name);
        }
    }
}
