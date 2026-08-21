using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using NuvioTV.Core.Media;
using NuvioTV.Core.Models;
using NuvioTV.Core.Networking;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the repository ports against the JS sources:
    /// catalogRepository.js, metaRepository.js, streamRepository.js,
    /// subtitleRepository.js, homeCatalogs.js, addonLogoCache.js keys.
    /// </summary>
    public class RepositoriesTests
    {
        // ------------------------------------------------------------------
        // CatalogRepository
        // ------------------------------------------------------------------

        [Fact]
        public async Task Catalog_GetRowAsync_AssemblesRowAndMapsMetas()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/catalog/movie/top.json"] = JsonResponse(new
            {
                metas = new object[]
                {
                    new { id = "tt1", name = "One", type = "movie", poster = "http://p/1.jpg" },
                    new { type = "movie" }
                }
            });
            var repo = CreateCatalogRepo(handler);
            var addon = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "top", Name = "Top Movies", Type = "movie", ApiType = "movie" }
                });

            var row = await repo.GetRowAsync(addon, "movie", "top");

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://a.io/catalog/movie/top.json", request.RequestUri.ToString());
            Assert.Equal("org.a", row.AddonId);
            Assert.Equal("Addon A", row.AddonName);
            Assert.Equal("https://a.io", row.AddonBaseUrl);
            Assert.Equal("top", row.CatalogId);
            Assert.Equal("Top Movies", row.CatalogName);
            Assert.Equal("movie", row.ApiType);
            Assert.False(row.IsLoading);
            Assert.True(row.HasMore);
            Assert.Equal(0, row.CurrentPage);
            Assert.True(row.SupportsSkip);
            Assert.Equal(2, row.Items.Count);
            Assert.Equal("tt1", row.Items[0].Id);
            Assert.Equal("One", row.Items[0].Name);
            Assert.Equal("http://p/1.jpg", row.Items[0].Poster);
            Assert.Equal("Untitled", row.Items[1].Name);
            Assert.Equal("", row.Items[1].Description);
        }

        [Fact]
        public async Task Catalog_SkipPagingFlags_FollowSupportsSkip()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/catalog/movie/top/skip=250.json"] = JsonResponse(new
            {
                metas = new object[] { new { id = "tt1", name = "One" } }
            });
            var repo = CreateCatalogRepo(handler);
            var addon = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[] { new AddonCatalog { Id = "top", Name = "Top", Type = "movie", ApiType = "movie" } });

            var page = await repo.GetRowAsync(addon, "movie", "top", skip: 250);

            Assert.Equal(2, page.CurrentPage);
            Assert.True(page.HasMore);

            handler.Stub.Clear();
            handler.Stub["https://a.io/catalog/series/top.json"] = JsonResponse(new
            {
                metas = new object[] { new { id = "tt2", name = "Two" } }
            });
            var noSkip = await repo.GetRowAsync(addon, "series", "top", supportsSkip: false);

            Assert.Equal("https://a.io/catalog/series/top.json",
                handler.Requests.Last().RequestUri.ToString());
            Assert.False(noSkip.SupportsSkip);
            Assert.False(noSkip.HasMore); // items exist but supportsSkip=false
        }

        [Fact]
        public async Task Catalog_HasMore_FalseWhenNoItems()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/catalog/movie/top.json"] =
                JsonResponse(new { metas = Array.Empty<object>() });
            var repo = CreateCatalogRepo(handler);
            var addon = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[] { new AddonCatalog { Id = "top", Name = "Top", Type = "movie", ApiType = "movie" } });

            var row = await repo.GetRowAsync(addon, "movie", "top");

            Assert.True(row.SupportsSkip);
            Assert.False(row.HasMore); // supportsSkip=true but zero items
        }

        [Fact]
        public async Task Catalog_CachesRowsByKeyIncludingExtraArgs()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/catalog/movie/top.json"] =
                JsonResponse(new { metas = new object[] { new { id = "tt1", name = "One" } } });
            handler.Stub["https://a.io/catalog/movie/top/genre=Action.json"] =
                JsonResponse(new { metas = new object[] { new { id = "tt2", name = "Two" } } });
            var repo = CreateCatalogRepo(handler);
            var addon = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[] { new AddonCatalog { Id = "top", Name = "Top", Type = "movie", ApiType = "movie" } });

            var first = await repo.GetRowAsync(addon, "movie", "top");
            var cached = await repo.GetRowAsync(addon, "movie", "top");
            Assert.Same(first, cached);
            Assert.Single(handler.Requests);

            var withGenre = await repo.GetRowAsync(
                addon, "movie", "top",
                extraArgs: new Dictionary<string, string> { ["genre"] = "Action" });

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("https://a.io/catalog/movie/top/genre=Action.json",
                handler.Requests.Last().RequestUri.ToString());
            Assert.Equal("tt2", withGenre.Items[0].Id);

            // Cache key parity: sorted args joined "k=v" after skip.
            Assert.Equal("org.a_movie_top_0_genre=Action",
                CatalogRepository.BuildCacheKey("org.a", "movie", "top", 0,
                    new Dictionary<string, string> { ["genre"] = "Action" }));
        }

        // ------------------------------------------------------------------
        // MetaRepository
        // ------------------------------------------------------------------

        [Fact]
        public async Task Meta_GetAsync_NormalizesEscapesAndCaches()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/meta/series/tt1.json"] = JsonResponse(new
            {
                meta = new
                {
                    id = "tt1",
                    type = "series",
                    name = "Foo \\' Bar",
                    description = "Said \\\"hello\\\"",
                    releaseInfo = "2020\\u20132024",
                    genres = new object[] { "Drama" },
                    videos = new object[] { new { id = "tt1:1:1", title = "Pilot", season = 1, episode = 1 } }
                }
            });
            var client = CreateClient(handler);
            var repo = new MetaRepository(client);

            var meta = await repo.GetAsync("https://a.io", "series", "tt1");

            Assert.NotNull(meta);
            Assert.Equal("Foo ' Bar", meta.Name); // normalizeDisplayText
            Assert.Equal("Said \"hello\"", meta.Description);
            // JS normalizeDisplayText only unescapes \' and \" — \u sequences stay verbatim.
            Assert.Equal("2020\\u20132024", meta.ReleaseInfo);
            Assert.Single(meta.Genres);
            Assert.Single(meta.Videos);
            Assert.Equal(1, meta.Videos[0].Season);

            var again = await repo.GetAsync("https://a.io", "series", "tt1");
            Assert.Same(meta, again);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Meta_GetAsync_ReturnsNullForMissingOrEmptyMeta()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/meta/movie/tt404.json"] = JsonResponse(new { meta = new { } });
            var repo = new MetaRepository(CreateClient(handler));

            var meta = await repo.GetAsync("https://a.io", "movie", "tt404");

            Assert.Null(meta); // js: { status: "error", code: 404 }
        }

        [Fact]
        public async Task Meta_GetFromAllAddons_RecoversOwnerTypeViaIdPrefix()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://owner.io/meta/series/tt123.json"] = JsonResponse(new
            {
                meta = new { id = "tt123", type = "series", name = "Owner" }
            });
            var repo = new MetaRepository(CreateClient(handler));
            // Manifest declares only `series` under an explicit tt prefix while the
            // forwarded row type is `channel` — the owner pass must recover series.
            var addon = MakeAddon(
                "org.owner", "Owner", "https://owner.io",
                resources: new[]
                {
                    new AddonResource
                    {
                        Name = "meta",
                        Types = new List<string> { "series" },
                        IdPrefixes = new List<string> { "tt" }
                    }
                });

            var meta = await repo.GetFromAllAddonsAsync("channel", "tt123", new[] { addon });

            Assert.NotNull(meta);
            Assert.Equal("Owner", meta.Name);
            Assert.Equal("https://owner.io/meta/series/tt123.json",
                Assert.Single(handler.Requests).RequestUri.ToString());
        }

        [Fact]
        public async Task Meta_GetFromAllAddons_ReturnsNullWhenNothingMatches()
        {
            var handler = new UrlStubHandler();
            var repo = new MetaRepository(CreateClient(handler));
            var addon = MakeAddon(
                "org.none", "None", "https://none.io",
                resources: new[]
                {
                    new AddonResource { Name = "stream", Types = new List<string> { "movie" } }
                });


            var meta = await repo.GetFromAllAddonsAsync("movie", "tt1", new[] { addon });

            Assert.Null(meta);
            Assert.Empty(handler.Requests);
        }

        [Theory]
        [InlineData("", "tt1:series:2", "series")] // id segment contains ':series:'
        [InlineData("weird", "x:tv:1", "tv")]
        [InlineData("weird", "tt1", "weird")]
        public void Meta_InferCanonicalType(string type, string id, string expected)
        {
            Assert.Equal(expected, MetaRepository.InferCanonicalType(type, id));
        }

        // ------------------------------------------------------------------
        // StreamRepository
        // ------------------------------------------------------------------

        [Fact]
        public async Task Streams_AllAddons_GroupShapeAndManifestOrder()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://a.io/stream/movie/tt1.json"] = JsonResponse(new
            {
                streams = new object[]
                {
                    new { name = "A direct", title = "A title", url = "http://cdn/a.mp4" }
                }
            });
            handler.Stub["https://b.io/stream/movie/tt1.json"] = JsonResponse(new
            {
                streams = new object[]
                {
                    new
                    {
                        name = "B torrent",
                        infoHash = "abc123",
                        fileIdx = 2,
                        quality = "1080p",
                        qualityValue = 100,
                        behaviorHints = new { notWebReady = true },
                        subtitles = new object[] { new { url = "http://s/sub.srt", lang = "eng" } }
                    }
                }
            });
            var repo = new StreamRepository(CreateClient(handler));
            var addons = new[]
            {
                MakeAddon("org.b", "Addon B", "https://b.io",
                    logo: "http://b/logo.png",
                    resources: new[] { new AddonResource { Name = "stream", Types = new List<string> { "movie" } } }),
                MakeAddon("org.metaonly", "Meta Only", "https://m.io",
                    resources: new[] { new AddonResource { Name = "meta", Types = new List<string> { "movie" } } }),
                MakeAddon("org.a", "Addon A", "https://a.io",
                    logo: "http://a/logo.png",
                    resources: new[] { new AddonResource { Name = "stream", Types = new List<string> { "movie" } } })
            };

            var groups = await repo.GetStreamsFromAllAddonsAsync(addons, "movie", "tt1");

            // Meta-only addon skipped; remaining groups keep manifest order
            // (sorted by addonOrderIndex, like the JS sort).
            Assert.Equal(2, groups.Count);
            Assert.Equal("org.b", groups[0].AddonId);
            Assert.Equal(0, groups[0].AddonOrderIndex);
            Assert.Equal("org.a", groups[1].AddonId);

            var b = groups[0];
            Assert.Equal("Addon B", b.AddonName);
            Assert.Equal("https://b.io", b.AddonBaseUrl);
            Assert.Equal("B torrent", b.Streams[0].Name);
            Assert.Equal("abc123", b.Streams[0].InfoHash);
            Assert.Equal(2, b.Streams[0].FileIdx);
            Assert.Equal("1080p", b.Streams[0].Quality);
            Assert.Equal(100, b.Streams[0].QualityValue);
            Assert.True(b.Streams[0].BehaviorHints.NotWebReady);
            Assert.Single(b.Streams[0].Subtitles);
            Assert.Equal("http://s/sub.srt", b.Streams[0].Subtitles[0].Url);
            Assert.Equal("eng", b.Streams[0].Subtitles[0].Lang);
            Assert.Equal("Addon B", b.Streams[0].AddonName);
            Assert.Equal("http://b/logo.png", b.Streams[0].AddonLogo);
        }

        [Fact]
        public async Task Streams_EmptyEndpointFallsBackToInlineMetaStreams()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://cloud.io/stream/movie/tt9.json"] = JsonResponse(new { streams = Array.Empty<object>() });
            handler.Stub["https://cloud.io/meta/movie/tt9.json"] = JsonResponse(new
            {
                meta = new
                {
                    id = "tt9",
                    videos = new object[]
                    {
                        new
                        {
                            id = "tt9",
                            streams = new object[]
                            {
                                new { title = "Cloud copy", infoHash = "deadbeef" },
                                new { title = "No locator" }
                            }
                        }
                    }
                }
            });
            var repo = new StreamRepository(CreateClient(handler));
            var addon = MakeAddon(
                "org.cloud", "Cloud", "https://cloud.io",
                resources: new[]
                {
                    new AddonResource { Name = "stream", Types = new List<string> { "movie" } },
                    new AddonResource { Name = "meta", Types = new List<string> { "movie" } }
                });

            var groups = await repo.GetStreamsFromAllAddonsAsync(new[] { addon }, "movie", "tt9");

            Assert.Single(groups);
            var stream = Assert.Single(groups[0].Streams);
            Assert.Equal("Cloud copy", stream.Title);
            Assert.Equal("deadbeef", stream.InfoHash);
            Assert.Equal(2, handler.Requests.Count); // stream endpoint, then meta fallback

            var urls = handler.Requests.Select(request => request.RequestUri.ToString()).ToList();
            Assert.Contains("https://cloud.io/stream/movie/tt9.json", urls);
            Assert.Contains("https://cloud.io/meta/movie/tt9.json", urls);
        }

        [Fact]
        public async Task Streams_MetaOnlyOtherCatalog_TriesInlineStreams()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://debrid.io/meta/other/dmm%3A42.json"] = JsonResponse(new
            {
                meta = new
                {
                    id = "dmm:42",
                    videos = new object[]
                    {
                        new
                        {
                            id = "dmm:42",
                            streams = new object[] { new { externalUrl = "http://ext/file" } }
                        }
                    }
                }
            });
            var repo = new StreamRepository(CreateClient(handler));
            var addon = MakeAddon(
                "org.debrid", "Debrid", "https://debrid.io",
                resources: new[] { new AddonResource { Name = "meta", Types = new List<string> { "other" } } });

            var groups = await repo.GetStreamsFromAllAddonsAsync(new[] { addon }, "other", "dmm:42");

            Assert.Single(groups);
            Assert.Equal("http://ext/file", Assert.Single(groups[0].Streams).ExternalUrl);
        }

        [Fact]
        public async Task Streams_FailingAddonIsSkippedOthersStillResolve()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://bad.io/stream/movie/tt1.json"] = JsonResponse(new { error = "boom" }, HttpStatusCode.InternalServerError);
            handler.Stub["https://good.io/stream/movie/tt1.json"] = JsonResponse(new
            {
                streams = new object[] { new { url = "http://cdn/good.mp4" } }
            });
            var repo = new StreamRepository(CreateClient(handler));
            var addons = new[]
            {
                MakeAddon("org.bad", "Bad", "https://bad.io",
                    resources: new[] { new AddonResource { Name = "stream", Types = new List<string> { "movie" } } }),
                MakeAddon("org.good", "Good", "https://good.io",
                    resources: new[] { new AddonResource { Name = "stream", Types = new List<string> { "movie" } } })
            };

            var groups = await repo.GetStreamsFromAllAddonsAsync(addons, "movie", "tt1");

            var group = Assert.Single(groups);
            Assert.Equal("org.good", group.AddonId);
        }

        [Fact]
        public async Task Streams_StreamErrorDoesNotTriggerInlineFallback()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://bad.io/stream/movie/tt1.json"] =
                JsonResponse(new { error = "boom" }, HttpStatusCode.InternalServerError);
            var repo = new StreamRepository(CreateClient(handler));
            var addon = MakeAddon(
                "org.bad", "Bad", "https://bad.io",
                resources: new[]
                {
                    new AddonResource { Name = "stream", Types = new List<string> { "movie" } },
                    new AddonResource { Name = "meta", Types = new List<string> { "movie" } }
                });

            var groups = await repo.GetStreamsFromAllAddonsAsync(new[] { addon }, "movie", "tt1");

            Assert.Empty(groups);
            Assert.Single(handler.Requests); // failed stream call, NO meta fallback
        }

        [Theory]
        [InlineData("tt123:1:2", "tt123")]
        [InlineData("dmm:42", "dmm:42")] // non-tt first segment needs 2 segments — nothing dropped
        [InlineData("tt123", "tt123")]
        [InlineData("some:id:7:3", "some:id")]
        [InlineData("", "")]
        public void Streams_BuildContentLevelMetaId(string videoId, string expected)
        {
            Assert.Equal(expected, StreamRepository.BuildContentLevelMetaId(videoId));
        }

        // ------------------------------------------------------------------
        // Subtitle helpers + SubtitleRepository
        // ------------------------------------------------------------------

        [Fact]
        public void Subtitles_Candidates_SeriesPrefersVideoIdThenTypedImdbIds()
        {
            var candidates = SubtitleCandidates.BuildSubtitleIdCandidates(
                "series", new[] { "tt123", "kai:id" }, videoId: "tt123:1:2",
                season: 1, episode: 2);
            Assert.Equal(new[] { "tt123:1:2" }, candidates);

            var withoutVideo = SubtitleCandidates.BuildSubtitleIdCandidates(
                "series", new[] { "tt123", "other" }, season: 1, episode: 2);
            Assert.Equal(new[] { "tt123:1:2" }, withoutVideo);

            var noEpisodeNumbers = SubtitleCandidates.BuildSubtitleIdCandidates(
                "series", new[] { "tt123", "other" });
            Assert.Equal(new[] { "tt123", "other" }, noEpisodeNumbers);
        }

        [Fact]
        public void Subtitles_Candidates_MovieListsIdsAndAppliesPrefixes()
        {
            Assert.Equal(
                new[] { "tt1", "aa2" },
                SubtitleCandidates.BuildSubtitleIdCandidates("movie", new[] { "tt1", "aa2" }));
            Assert.Equal(
                new[] { "tt1" },
                SubtitleCandidates.BuildSubtitleIdCandidates(
                    "movie", new[] { "tt1", "aa2" }, idPrefixes: new[] { "tt" }));
            Assert.Empty(SubtitleCandidates.BuildSubtitleIdCandidates(
                "movie", new[] { "tt1" }, idPrefixes: new[] { "zz" }));
        }

        [Fact]
        public void Subtitles_SelectCanonicalCinemetaId_MatchesTitleYearUniquely()
        {
            Meta Item(string id, string type, string name, string releaseInfo) =>
                new Meta(id, type, name, null, null, null, "", Array.Empty<string>(), Array.Empty<MetaVideo>(), releaseInfo);

            var items = new[]
            {
                Item("tt111", "movie", "The Matrix Reloaded", "1999"),
                Item("tt222", "series", "The Matrix", "1999"),
                Item("tt333", "movie", "the  matrix", "Released 1999")
            };

            Assert.Equal("tt333", SubtitleCandidates.SelectCanonicalCinemetaId(items, "Movie", "THE MATRIX", "(1999)"));
            Assert.Equal("", SubtitleCandidates.SelectCanonicalCinemetaId(
                new[] { Item("tt111", "movie", "The Matrix", "1999"), Item("tt333", "movie", "The Matrix", "1999") },
                "movie", "The Matrix", "1999")); // ambiguous
            Assert.Equal("", SubtitleCandidates.SelectCanonicalCinemetaId(items, "movie", "Unknown", "1999"));
            Assert.Equal("", SubtitleCandidates.SelectCanonicalCinemetaId(items, "movie", "The Matrix", ""));
        }

        [Fact]
        public void Subtitles_ResourceMatching_Helpers()
        {
            Assert.True(SubtitleRepository.IsSubtitleResource("Subtitles"));
            Assert.True(SubtitleRepository.IsSubtitleResource("subtitle"));
            Assert.False(SubtitleRepository.IsSubtitleResource("stream"));

            Assert.Equal(new[] { "series", "tv" }, SubtitleRepository.CompatibleTypes("tv"));
            Assert.Equal(new[] { "movie" }, SubtitleRepository.CompatibleTypes(" Movie "));

            Assert.Equal("tt1", SubtitleRepository.NormalizeIdForLookup(" tt1:1:2 "));
            Assert.Equal("", SubtitleRepository.NormalizeIdForLookup(null));

            var typedResource = new AddonResource
            {
                Name = "subtitles",
                Types = new List<string> { "Series" },
                IdPrefixes = new List<string> { "tt" }
            };
            Assert.True(SubtitleRepository.SupportsType(typedResource, "series", "tt1:1:2"));
            Assert.False(SubtitleRepository.SupportsType(typedResource, "movie", "tt1"));
            Assert.False(SubtitleRepository.SupportsType(typedResource, "series", "kai1"));
        }

        [Fact]
        public async Task Subtitles_GetSubtitlesAsync_MergesDedupesAttachesAddonIdentity()
        {
            var handler = new UrlStubHandler();
            handler.Stub["https://sub1.io/subtitles/movie/tt1.json"] = JsonResponse(new
            {
                subtitles = new object[]
                {
                    new { id = "s1", url = "http://s/1.srt", lang = "eng" },
                    new { url = "http://s/shared.srt", lang = "Eng" }
                }
            });
            handler.Stub["https://sub2.io/subtitles/movie/tt1.json"] = JsonResponse(new
            {
                subtitles = new object[]
                {
                    // JS concatenates addon results without cross-addon dedupe
                    // (url+lang dedupe is per-addon only) — same url appears twice.
                    new { id = "shared-id", url = "http://s/shared.srt", lang = "eng" },
                    new { url = "http://s/2.vtt" } // no lang → deterministic id + "unknown"
                }
            });
            var client = CreateClient(handler);
            var repo = new SubtitleRepository(client, new CatalogRepository(client));
            var addons = new[]
            {
                MakeAddon("org.sub1", "Sub One", "https://sub1.io",
                    logo: "http://sub1/logo.png",
                    resources: new[] { new AddonResource { Name = "subtitles", Types = new List<string> { "movie" } } }),
                MakeAddon("org.sub2", "Sub Two", "https://sub2.io",
                    logo: "http://sub2/logo.png",
                    resources: new[] { new AddonResource { Name = "subtitle", Types = new List<string> { "movie" } } }),
                MakeAddon("org.nosub", "No Sub", "https://nosub.io",
                    resources: new[] { new AddonResource { Name = "stream", Types = new List<string> { "movie" } } })
            };

            var merged = await repo.GetSubtitlesAsync(addons, "movie", "tt1");
            Assert.Equal(4, merged.Count);
            Assert.Equal("s1", merged[0].Id);
            Assert.Equal("eng", merged[0].Lang);
            Assert.Equal("Sub One", merged[0].AddonName);
            Assert.Equal("http://sub1/logo.png", merged[0].AddonLogo);
            Assert.Equal("http://s/shared.srt", merged[1].Url);
            Assert.NotEqual("shared-id", merged[1].Id); // sub1 entry has no id → deterministic
            Assert.Equal("Sub One", merged[1].AddonName);
            Assert.Equal("shared-id", merged[2].Id); // sub2's copy, concatenated per JS
            Assert.Equal("Sub Two", merged[2].AddonName);
            Assert.Equal("unknown", merged[3].Lang);
            Assert.Equal($"unk-{SubtitleRepository.MakeDeterministicId("http://s/2.vtt")}", merged[3].Id);
            Assert.Equal("Sub Two", merged[3].AddonName);

            // Only the two subtitle addons were queried.
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request =>
                Assert.StartsWith("https://sub", request.RequestUri.ToString()));
        }

        [Fact]
        public void Subtitles_MakeDeterministicId_IsStableJs32BitHash()
        {
            Assert.Equal(SubtitleRepository.MakeDeterministicId("abc"),
                SubtitleRepository.MakeDeterministicId("abc"));
            Assert.NotEqual("0", SubtitleRepository.MakeDeterministicId("abc"));
        }

        // ------------------------------------------------------------------
        // HomeCatalogs
        // ------------------------------------------------------------------

        [Fact]
        public void HomeCatalogs_ResolveHomeRows_FiltersRequiredExtrasKeepsOptional()
        {
            var requiredSearch = new AddonCatalogExtra { Name = "search", IsRequired = true };
            var optionalGenre = new AddonCatalogExtra { Name = "genre", IsRequired = false, Options = new List<string> { "Action" } };
            var addonA = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "top", Name = "Popular", Type = "movie", ApiType = "movie" },
                    new AddonCatalog { Id = "search", Name = "Search", Type = "movie", ApiType = "movie", Extra = new List<AddonCatalogExtra> { requiredSearch } },
                    new AddonCatalog { Id = "byGenre", Name = "By Genre", Type = "movie", ApiType = "movie", Extra = new List<AddonCatalogExtra> { optionalGenre } }
                });
            var addonB = MakeAddon(
                "org.b", "Addon B", "https://b.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "top", Name = "B Popular", Type = "series", ApiType = "series" }
                });

            var rows = HomeCatalogs.ResolveHomeRows(new[] { addonA, addonB });

            // Required-extra catalogs belong to search/discover; optional extras stay.
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "org.a_movie_top", "org.a_movie_byGenre", "org.b_series_top" },
                rows.Select(row => row.Key));
            Assert.All(rows, row => Assert.False(row.IsDisabled));
            Assert.All(rows, row => Assert.False(row.IsCollection));
            Assert.False(rows[0].CanMoveUp); // first entry cannot move up
            Assert.True(rows[0].CanMoveDown);
            Assert.Equal("Popular", rows[0].OriginalCatalogName);
            Assert.True(rows[^1].CanMoveUp);
            Assert.False(rows[^1].CanMoveDown);
        }

        [Fact]
        public void HomeCatalogs_SavedOrderDisabledKeysAndCustomTitlesApply()
        {
            var addonA = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "top", Name = "A Top", Type = "movie", ApiType = "movie" }
                });
            var addonB = MakeAddon(
                "org.b", "Addon B", "https://b.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "featured", Name = "B Featured", Type = "series", ApiType = "series" }
                });
            var disabledSet = new[] { HomeCatalogs.BuildCatalogDisableKey("https://a.io", "movie", "top", "A Top") };

            var rows = HomeCatalogs.BuildOrderedCatalogItems(
                new[] { addonA, addonB },
                savedOrderKeys: new[] { "org.b_series_featured" },
                disabledKeys: disabledSet,
                customTitles: new Dictionary<string, string> { ["org.b_series_featured"] = "My Featured" });

            // JS keeps disabled entries in the ordered list and flags them
            // (isDisabled) — it does not drop them.
            Assert.Equal(2, rows.Count);
            Assert.Equal("org.b_series_featured", rows[0].Key);
            Assert.Equal("My Featured", rows[0].CatalogName);
            Assert.False(rows[0].IsDisabled);
            Assert.True(rows[1].IsDisabled);
            Assert.Equal("org.a_movie_top", rows[1].Key);
        }

        [Fact]
        public void HomeCatalogs_CollectionsAppendWithFolderLabels()
        {
            var addon = MakeAddon(
                "org.a", "Addon A", "https://a.io",
                catalogs: new[]
                {
                    new AddonCatalog { Id = "top", Name = "Top", Type = "movie", ApiType = "movie" }
                });

            var rows = HomeCatalogs.BuildOrderedHomeCatalogItems(
                new[] { addon },
                collections: new[]
                {
                    new HomeCollection { Id = "col1", Title = "My List", FolderCount = 1 },
                    new HomeCollection { Id = "col2", Title = "Big", FolderCount = 4 }
                });

            Assert.Equal(3, rows.Count);
            Assert.True(rows[1].IsCollection);
            Assert.Equal("collection_col1", rows[1].Key);
            Assert.Equal("1 folder", rows[1].AddonName);
            Assert.Equal("My List", rows[1].CatalogName);
            Assert.Equal("4 folders", rows[2].AddonName);
            Assert.Equal("col1", rows[1].CollectionId);
        }

        [Fact]
        public void HomeCatalogs_KeyHelpersMatchJs()
        {
            Assert.True(HomeCatalogs.CatalogRequiresExtras(
                new AddonCatalog { Extra = new List<AddonCatalogExtra> { new AddonCatalogExtra { IsRequired = true } } }));
            Assert.False(HomeCatalogs.CatalogRequiresExtras(
                new AddonCatalog { Extra = new List<AddonCatalogExtra> { new AddonCatalogExtra { IsRequired = false } } }));
            Assert.False(HomeCatalogs.CatalogRequiresExtras(new AddonCatalog()));

            Assert.Equal("a_movie_top", HomeCatalogs.BuildCatalogOrderKey("a", "movie", "top"));
            Assert.Equal("https://a.io_movie_top_Top", HomeCatalogs.BuildCatalogDisableKey("https://a.io", "movie", "top", "Top"));
            Assert.Equal("collection_x", HomeCatalogs.BuildCollectionOrderKey(" x "));
            Assert.Equal("", HomeCatalogs.BuildCollectionOrderKey("  "));
            Assert.Equal("Movie", HomeCatalogs.ToDisplayTypeLabel("movie"));
            Assert.Equal("Series", HomeCatalogs.ToDisplayTypeLabel(" SERIES "));
            Assert.Equal("", HomeCatalogs.ToDisplayTypeLabel(""));
        }

        // ------------------------------------------------------------------
        // AddonLogoCacheKeys
        // ------------------------------------------------------------------

        [Fact]
        public void AddonLogoCacheKeys_ConstantsAndNormalization()
        {
            Assert.Equal("nuvio.stream.addonLogoCache.v1", AddonLogoCacheKeys.StorageKey);
            Assert.Equal(36, AddonLogoCacheKeys.CacheLimit);
            Assert.Equal(12, AddonLogoCacheKeys.TvCacheLimit);
            Assert.Equal(140000, AddonLogoCacheKeys.MaxSerializedLength);

            Assert.Equal("http://l/logo.png", AddonLogoCacheKeys.NormalizeUrl(" http://l/logo.png "));

            var lookup = new Dictionary<string, string>();
            AddonLogoCacheKeys.RememberAddonLogoLookup(lookup, "Addon A", " http://a/logo.png ");
            Assert.Equal("addon a", Assert.Single(lookup).Key);
            Assert.Equal("http://a/logo.png", lookup["addon a"]);
            // Empty names/logos are dropped (js rememberAddonLogoLookup).
            AddonLogoCacheKeys.RememberAddonLogoLookup(lookup, "  ", "http://x/l.png");
            AddonLogoCacheKeys.RememberAddonLogoLookup(lookup, "X", "   ");
            Assert.Single(lookup);

            Assert.Equal("http://a/logo.png",
                AddonLogoCacheKeys.ResolveAddonLogo(" ADDON-A ".Replace("-", " "), lookup));
            Assert.Equal("", AddonLogoCacheKeys.ResolveAddonLogo("missing", lookup));
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static CatalogRepository CreateCatalogRepo(UrlStubHandler handler)
        {
            return new CatalogRepository(CreateClient(handler));
        }

        private static StremioAddonClient CreateClient(UrlStubHandler handler)
        {
            return new StremioAddonClient(
                new NuvioHttpClient(new HttpClient(handler), new NullSessionTokens()));
        }

        private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        private static Addon MakeAddon(
            string id, string displayName, string baseUrl,
            string logo = null,
            IReadOnlyList<AddonCatalog> catalogs = null,
            IReadOnlyList<AddonResource> resources = null)
        {
            return new Addon(
                id, id, displayName, "1.0.0", "", logo, baseUrl,
                catalogs ?? Array.Empty<AddonCatalog>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                resources ?? Array.Empty<AddonResource>());
        }

        private sealed class NullSessionTokens : ISessionTokenProvider
        {
            public string GetAccessToken() => null;
            public string GetRefreshToken() => null;
            public bool HasTokens() => false;
            public Task<bool> TryRefreshAsync(bool force = false) => Task.FromResult(false);
        }

        /// <summary>
        /// Routes by absolute request URL — deterministic under the unbounded
        /// parallelism the repositories inherit from Promise.all in JS.
        /// </summary>
        private sealed class UrlStubHandler : HttpMessageHandler
        {
            public Dictionary<string, HttpResponseMessage> Stub { get; } =
                new Dictionary<string, HttpResponseMessage>(StringComparer.Ordinal);

            public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (this)
                {
                    Requests.Add(request);
                    var url = request.RequestUri.ToString();
                    if (!Stub.TryGetValue(url, out var response))
                    {
                        throw new InvalidOperationException($"Unexpected request URL: {url}");
                    }
                    return Task.FromResult(response);
                }
            }
        }
    }
}
