using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Debrid;
using NuvioTV.Core.Streams;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the debrid stack against js/core/debrid/*:
    /// provider registry (debridProviders.js), file-selection matrix
    /// (debridFileSelection.js), resolver request/response fixtures
    /// (directDebridResolver.js), and template values
    /// (directDebridStreamPresentation.js buildTemplateValues).
    /// </summary>
    public class DebridStackTests
    {
        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static HttpResponseMessage Json(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage StatusJson(int status, object payload)
        {
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        private sealed class StubSettings : IDebridSettingsProvider
        {
            public DebridSettingsSnapshot Snapshot { get; set; } = new DebridSettingsSnapshot();

            public DebridSettingsSnapshot Get()
            {
                return Snapshot;
            }
        }

        private static DebridSettingsSnapshot TorboxSettings(string key = "tb-key-1")
        {
            return new DebridSettingsSnapshot { Enabled = true, TorboxApiKey = key };
        }

        /// <summary>Direct-debrid stream: cached torbox resolve descriptor.</summary>
        private static DebridStream TorboxStream(
            string magnet = "magnet:?xt=urn:btih:abc123&dn=Movie.2020",
            string filename = "Movie.2020.1080p.mkv")
        {
            return new DebridStream
            {
                Name = "Torrent row",
                Title = "Movie 2020",
                InfoHash = "abc123",
                ClientResolve = new DebridClientResolve
                {
                    Type = "debrid",
                    Service = "torbox",
                    MagnetUri = magnet,
                    Filename = filename,
                    Title = "Movie 2020",
                    IsCached = true
                }
            };
        }

        private static DebridRemoteFile File(
            string name, double size, string id = null, string mime = null, string link = null)
        {
            return new DebridRemoteFile { Id = id, Name = name, Size = size, MimeType = mime, Link = link };
        }

        private static void AssertBearer(HttpRequestMessage request, string key)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(key, request.Headers.Authorization?.Parameter);
        }

        // ------------------------------------------------------------------
        // Provider registry (js debridProviders.js)
        // ------------------------------------------------------------------

        [Fact]
        public void Registry_AllListsThreeProvidersInOrder()
        {
            Assert.Equal(
                new[] { "torbox", "premiumize", "realdebrid" },
                DebridProviders.All().Select(provider => provider.Id).ToArray());
        }

        [Fact]
        public void Registry_VisibleHidesRealDebrid()
        {
            Assert.Equal(
                new[] { "torbox", "premiumize" },
                DebridProviders.Visible().Select(provider => provider.Id).ToArray());
        }

        [Fact]
        public void Registry_ByIdNormalizesAliases()
        {
            Assert.Equal("realdebrid", DebridProviders.ById("Real-Debrid").Id);
            Assert.Equal("realdebrid", DebridProviders.ById(" rd ").Id);
            Assert.Equal("torbox", DebridProviders.ById("TB").Id);
            Assert.Equal("premiumize", DebridProviders.ById("pm").Id);
            Assert.Null(DebridProviders.ById("alldebrid"));
        }

        [Fact]
        public void Registry_SupportsChecksCapabilities()
        {
            Assert.True(DebridProviders.Supports("torbox", DebridCapabilities.LocalTorrentResolve));
            Assert.True(DebridProviders.Supports("premiumize", DebridCapabilities.CloudLibrary));
            Assert.True(DebridProviders.Supports("realdebrid", DebridCapabilities.ClientResolve));
            Assert.False(DebridProviders.Supports("realdebrid", DebridCapabilities.LocalTorrentResolve));
            Assert.False(DebridProviders.Supports("unknown", DebridCapabilities.ClientResolve));
        }

        [Fact]
        public void Registry_DisplayNameFallsBackToTitleCase()
        {
            Assert.Equal("Torbox", DebridProviders.DisplayName("torbox"));
            Assert.Equal("Real-Debrid", DebridProviders.DisplayName("realdebrid"));
            Assert.Equal("Acme Boxes", DebridProviders.DisplayName("acme-boxes"));
            Assert.Equal("Debrid", DebridProviders.DisplayName(""));
            Assert.Equal("Debrid", DebridProviders.DisplayName(null));
        }

        [Fact]
        public void Registry_ConfiguredServicesSkipEmptyKeysAndHiddenProviders()
        {
            var settings = new Dictionary<string, string>
            {
                ["torboxApiKey"] = "  tbk  ",
                ["premiumizeApiKey"] = "",
                ["realDebridApiKey"] = "hidden-key"
            };

            var services = DebridProviders.ConfiguredServices(settings);

            var service = Assert.Single(services);
            Assert.Equal("torbox", service.Provider.Id);
            Assert.Equal("tbk", service.ApiKey);
        }

        [Fact]
        public void Registry_PreferredResolverServiceHonorsPreferenceThenFirst()
        {
            var both = new Dictionary<string, string>
            {
                ["torboxApiKey"] = "tbk",
                ["premiumizeApiKey"] = "pmk"
            };
            Assert.Equal(
                "premiumize",
                DebridProviders.PreferredResolverService(new Dictionary<string, string>(both)
                {
                    ["preferredResolverProviderId"] = "PM"
                }).Provider.Id);
            Assert.Equal("torbox", DebridProviders.PreferredResolverService(both).Provider.Id);
            Assert.Null(DebridProviders.PreferredResolverService(new Dictionary<string, string>()));
        }

        // ------------------------------------------------------------------
        // File selection (js debridFileSelection.js)
        // ------------------------------------------------------------------

        [Fact]
        public void FileSelection_BuildEpisodePatternsFormatsSxxEyy()
        {
            Assert.Equal(new[] { "s01e02", "1x02", "1x2" }, DebridFileSelection.BuildEpisodePatterns(1, 2));
            Assert.Empty(DebridFileSelection.BuildEpisodePatterns(null, 2));
            Assert.Empty(DebridFileSelection.BuildEpisodePatterns(1, null));
            Assert.Empty(DebridFileSelection.BuildEpisodePatterns(0, 0));
        }

        [Fact]
        public void FileSelection_NormalizeFileNameStripsExtensionAndNoise()
        {
            Assert.Equal("movie 2020 1080p", DebridFileSelection.NormalizeFileName("/a/b/Movie.2020.1080p.mkv"));
            Assert.Equal("show s01e02", DebridFileSelection.NormalizeFileName("Show-S01E02."));
            Assert.Equal("", DebridFileSelection.NormalizeFileName(""));
            Assert.Equal("", DebridFileSelection.NormalizeFileName(null));
        }

        [Fact]
        public void FileSelection_MatchesResolveFilenameFirst()
        {
            var files = new[]
            {
                File("Extras.Sample.mkv", 100),
                File("Movie.2020.1080p.mkv", 2000),
                File("Movie.2020.2160p.mkv", 9000)
            };
            var resolve = new DebridClientResolve { Filename = "movie 2020 1080p" };

            var picked = DebridFileSelection.SelectFile(files, resolve, null, null, "torbox");

            Assert.Equal("Movie.2020.1080p.mkv", DebridFileSelection.GetDisplayName(picked));
        }

        [Fact]
        public void FileSelection_MatchesEpisodePatternOverLargest()
        {
            var files = new[]
            {
                File("Other.Show.S01E01.mkv", 9000),
                File("Show.S01E02.mkv", 10)
            };
            var resolve = new DebridClientResolve();

            var picked = DebridFileSelection.SelectFile(files, resolve, 1, 2, "torbox");

            Assert.Equal("Show.S01E02.mkv", DebridFileSelection.GetDisplayName(picked));
        }

        [Fact]
        public void FileSelection_FileIdxZeroBasedThenOneBasedThenId()
        {
            var files = new[]
            {
                File("a.sample", 1),          // not playable — blocks zero-based pick below
                File("first.mkv", 10),
                File("second.mkv", 20)
            };
            var resolve = new DebridClientResolve { FileIdx = 0 };
            // Zero-based index 0 is not playable → falls through to largest playable.
            Assert.Equal("second.mkv",
                DebridFileSelection.GetDisplayName(DebridFileSelection.SelectFile(files, resolve, null, null, "torbox")));

            // One-based: idx=2 → list[1].
            var oneBased = new[] { File("first.mkv", 10), File("second.mkv", 20) };
            Assert.Equal("second.mkv",
                DebridFileSelection.GetDisplayName(DebridFileSelection.SelectFile(
                    oneBased, new DebridClientResolve { FileIdx = 2 }, null, null, "torbox")));

            // Id match: idx=7 matches file id "7".
            var byId = new[] { File("x.mkv", 10, id: "3"), File("y.mkv", 20, id: "7") };
            Assert.Equal("y.mkv",
                DebridFileSelection.GetDisplayName(DebridFileSelection.SelectFile(
                    byId, new DebridClientResolve { FileIdx = 7 }, null, null, "torbox")));
        }

        [Fact]
        public void FileSelection_FallsBackToLargestPlayableAndFiltersNonVideo()
        {
            var files = new[]
            {
                File("readme.txt", 99999),
                File("small.mkv", 5),
                File("big.mp4", 50)
            };

            var picked = DebridFileSelection.SelectFile(files, new DebridClientResolve(), null, null, "torbox");

            Assert.Equal("big.mp4", DebridFileSelection.GetDisplayName(picked));
        }

        [Fact]
        public void FileSelection_PremiumizeAcceptsLinkWithoutNameWhenMimeVideo()
        {
            var files = new[] { new DebridRemoteFile { Link = "https://p.me/dl", Size = 12 } };

            Assert.Null(DebridFileSelection.SelectFile(files, new DebridClientResolve(), null, null, "torbox"));
            var picked = DebridFileSelection.SelectFile(files, new DebridClientResolve(), null, null, "premiumize");

            Assert.NotNull(picked);
            Assert.Equal("https://p.me/dl", picked.Link);
        }

        [Fact]
        public void FileSelection_GetFileSizePrefersSizeOverBytes()
        {
            Assert.Equal(30, DebridFileSelection.GetFileSize(new DebridRemoteFile { Size = 30, Bytes = 10 }));
            Assert.Equal(10, DebridFileSelection.GetFileSize(new DebridRemoteFile { Bytes = 10 }));
            Assert.Equal(0, DebridFileSelection.GetFileSize(null));
        }

        // ------------------------------------------------------------------
        // Resolver (js directDebridResolver.js) — fixtures via RecordingHandler
        // ------------------------------------------------------------------

        [Fact]
        public async Task Resolve_DirectUrlStreamPassesThroughWithoutHttp()
        {
            var handler = new RecordingHandler();
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });
            var stream = new DebridStream { Url = "https://cdn.example/video.mkv" };

            var outcome = await resolver.ResolveAsync(stream);

            Assert.Equal(DebridResolveStatus.Success, outcome.Status);
            Assert.Same(stream, outcome.Stream);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Resolve_DisabledSettingsReturnsDisabled()
        {
            var handler = new RecordingHandler();
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = new DebridSettingsSnapshot() });

            var outcome = await resolver.ResolveAsync(TorboxStream());

            Assert.Equal(DebridResolveStatus.Disabled, outcome.Status);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Resolve_MissingApiKeyReturnsMissingApiKey()
        {
            var handler = new RecordingHandler();
            var resolver = new DirectDebridResolver(
                new HttpClient(handler),
                new StubSettings { Snapshot = new DebridSettingsSnapshot { Enabled = true } });

            var outcome = await resolver.ResolveAsync(TorboxStream());

            Assert.Equal(DebridResolveStatus.MissingApiKey, outcome.Status);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task Resolve_TorboxHappyPathCreatetorrentMylistRequestdl()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new { data = new { torrent_id = "123" } }));
            handler.Enqueue(Json(new
            {
                data = new
                {
                    data = new
                    {
                        files = new[]
                        {
                            new { id = "7", name = "Movie.2020.1080p.mkv", size = 2147483648d }
                        }
                    }
                }
            }));
            handler.Enqueue(Json(new { @data = "https://dl.torbox.com/Movie.2020.1080p.mkv" }));
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });

            var outcome = await resolver.ResolveAsync(TorboxStream());

            Assert.Equal(DebridResolveStatus.Success, outcome.Status);
            Assert.Equal("https://dl.torbox.com/Movie.2020.1080p.mkv", outcome.Stream.Url);
            Assert.Equal("Movie.2020.1080p.mkv", outcome.Stream.FilenameHint);
            Assert.Equal(2147483648L, outcome.Stream.VideoSizeHint);

            Assert.Equal(3, handler.Requests.Count);
            var create = handler.Requests[0];
            Assert.Equal("https://api.torbox.app/v1/api/torrents/createtorrent", create.RequestUri.ToString());
            Assert.Equal(HttpMethod.Post, create.Method);
            AssertBearer(create, "tb-key-1");
            Assert.Equal("https://api.torbox.app/v1/api/torrents/mylist?id=123&bypass_cache=true",
                handler.Requests[1].RequestUri.ToString());
            var requestDl = handler.Requests[2];
            Assert.Equal("https://api.torbox.app/v1/api/torrents/requestdl?token=tb-key-1&torrent_id=123&zip_link=false&redirect=false&append_name=false&file_id=7",
                requestDl.RequestUri.ToString());
            AssertBearer(requestDl, "tb-key-1");
        }

        [Fact]
        public async Task Resolve_TorboxCreateConflictMapsToNotCached()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(StatusJson(409, new { error = "not cached" }));
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });

            var outcome = await resolver.ResolveAsync(TorboxStream());

            Assert.Equal(DebridResolveStatus.NotCached, outcome.Status);
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Resolve_TorboxBadGatewayMapsToServiceDegraded()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(StatusJson(503, new { error = "upstream" }));
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });

            var outcome = await resolver.ResolveAsync(TorboxStream());

            Assert.Equal(DebridResolveStatus.ServiceDegraded, outcome.Status);
            Assert.Contains("HTTP 503", outcome.Detail);
        }

        [Fact]
        public async Task Resolve_LocalTorrentCacheCheckNotCachedShortCircuits()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new { success = true, @data = new { abc123 = false } }));
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });
            var stream = new DebridStream { Name = "row", InfoHash = "ABC123" };

            var outcome = await resolver.ResolveAsync(stream);

            Assert.Equal(DebridResolveStatus.NotCached, outcome.Status);
            var check = Assert.Single(handler.Requests);
            Assert.Equal("https://api.torbox.app/v1/api/torrents/checkcached?format=object",
                check.RequestUri.ToString());
            Assert.Equal(HttpMethod.Post, check.Method);
            AssertBearer(check, "tb-key-1");
        }

        [Fact]
        public async Task Resolve_LocalTorrentCacheCheckCachedProceedsToResolve()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new { success = true, @data = new { abc123 = true } }));
            handler.Enqueue(Json(new { data = new { torrent_id = "9" } }));
            handler.Enqueue(Json(new
            {
                data = new
                {
                    data = new
                    {
                        files = new[] { new { id = "1", name = "Film.2021.mkv", size = 5d } }
                    }
                }
            }));
            handler.Enqueue(Json(new { @data = "https://dl.torbox.com/Film.2021.mkv" }));
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });
            var stream = new DebridStream { Name = "row", InfoHash = "abc123", Sources = new List<string>() };

            var outcome = await resolver.ResolveAsync(stream);

            Assert.Equal(DebridResolveStatus.Success, outcome.Status);
            Assert.Equal(4, handler.Requests.Count);
            Assert.Contains("/checkcached", handler.Requests[0].RequestUri.ToString());
            Assert.Contains("/createtorrent", handler.Requests[1].RequestUri.ToString());
        }

        [Fact]
        public async Task Resolve_PremiumizeHappyPathPicksContentLink()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new
            {
                status = "success",
                content = new[]
                {
                    new { link = "https://p.me/small.mkv", size = 5d, mime = "video/x-matroska", name = "small.mkv" },
                    new { link = "https://p.me/Movie.2020.1080p.mkv", size = 2000d, mime = "video/x-matroska", name = "Movie.2020.1080p.mkv" }
                }
            }));
            var settings = new DebridSettingsSnapshot { Enabled = true, PremiumizeApiKey = "pm-key-1" };
            var resolver = new DirectDebridResolver(new HttpClient(handler), new StubSettings { Snapshot = settings });
            var stream = new DebridStream
            {
                Name = "row",
                ClientResolve = new DebridClientResolve
                {
                    Type = "debrid",
                    Service = "premiumize",
                    MagnetUri = "magnet:?xt=urn:btih:pm42",
                    IsCached = true
                }
            };

            var outcome = await resolver.ResolveAsync(stream);

            Assert.Equal(DebridResolveStatus.Success, outcome.Status);
            Assert.Equal("https://p.me/Movie.2020.1080p.mkv", outcome.Stream.Url);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://www.premiumize.me/api/transfer/directdl", request.RequestUri.ToString());
            AssertBearer(request, "pm-key-1");
            var body = handler.RequestBodies[0];
            Assert.Contains("src=magnet%3A%3Fxt%3Durn%3Abtih%3Apm42", body);
        }

        [Fact]
        public async Task Resolve_PremiumizeErrorMessageWithCacheMapsToNotCached()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new { status = "error", message = "This item is not cached", code = 42 }));
            var settings = new DebridSettingsSnapshot { Enabled = true, PremiumizeApiKey = "pm-key-1" };
            var resolver = new DirectDebridResolver(new HttpClient(handler), new StubSettings { Snapshot = settings });

            var outcome = await resolver.ResolveAsync(new DebridStream
            {
                Name = "row",
                ClientResolve = new DebridClientResolve
                {
                    Service = "premiumize",
                    MagnetUri = "magnet:?xt=urn:btih:pm42"
                }
            });

            Assert.Equal(DebridResolveStatus.NotCached, outcome.Status);
        }

        [Fact]
        public async Task Resolve_RealDebridHappyPathAddsSelectsUnrestricts()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(Json(new { id = "t42" }));
            handler.Enqueue(Json(new { files = new[] { new { id = "7", path = "/Movie.2020.1080p.mkv", bytes = 300d } } }));
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
            handler.Enqueue(Json(new { status = "downloaded", links = new[] { "https://rd/link/1" } }));
            handler.Enqueue(Json(new { download = "https://rd/dl/Movie.mkv", filename = "Movie.2020.1080p.mkv", filesize = 300d }));
            var settings = new DebridSettingsSnapshot { Enabled = true, RealDebridApiKey = "rd-key-1" };
            var resolver = new DirectDebridResolver(new HttpClient(handler), new StubSettings { Snapshot = settings });
            var stream = new DebridStream
            {
                Name = "row",
                ClientResolve = new DebridClientResolve
                {
                    Service = "realdebrid",
                    MagnetUri = "magnet:?xt=urn:btih:rd42",
                    Filename = "movie 2020 1080p"
                }
            };

            var outcome = await resolver.ResolveAsync(stream);

            Assert.Equal(DebridResolveStatus.Success, outcome.Status);
            Assert.Equal("https://rd/dl/Movie.mkv", outcome.Stream.Url);
            Assert.Equal("Movie.2020.1080p.mkv", outcome.Stream.FilenameHint);
            Assert.Equal(300, outcome.Stream.VideoSizeHint);
            Assert.Null(resolver.LastCleanupTask); // resolved → no delete cleanup

            Assert.Equal(5, handler.Requests.Count);
            Assert.Equal("https://api.real-debrid.com/rest/1.0/torrents/addMagnet",
                handler.Requests[0].RequestUri.ToString());
            Assert.Equal("https://api.real-debrid.com/rest/1.0/torrents/info/t42",
                handler.Requests[1].RequestUri.ToString());
            Assert.Equal("https://api.real-debrid.com/rest/1.0/torrents/selectFiles/t42",
                handler.Requests[2].RequestUri.ToString());
            Assert.Equal("https://api.real-debrid.com/rest/1.0/torrents/info/t42",
                handler.Requests[3].RequestUri.ToString());
            Assert.Equal("https://api.real-debrid.com/rest/1.0/unrestrict/link",
                handler.Requests[4].RequestUri.ToString());
            foreach (var request in handler.Requests)
            {
                AssertBearer(request, "rd-key-1");
            }
        }

        [Fact]
        public async Task Resolve_CachesSuccessWithinTtlAndReResolvesAfterExpiry()
        {
            var handler = new RecordingHandler();
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var settings = TorboxSettings();
            var resolver = new DirectDebridResolver(new HttpClient(handler), new StubSettings { Snapshot = settings });
            resolver.Clock = () => now;

            // First resolve: createtorrent + mylist + requestdl.
            handler.Enqueue(Json(new { data = new { torrent_id = "123" } }));
            handler.Enqueue(Json(new
            {
                data = new
                {
                    data = new
                    {
                        files = new[] { new { id = "7", name = "Movie.2020.1080p.mkv", size = 1d } }
                    }
                }
            }));
            handler.Enqueue(Json(new { @data = "https://dl.torbox.com/a.mkv" }));

            var first = await resolver.ResolveAsync(TorboxStream());
            Assert.Equal(DebridResolveStatus.Success, first.Status);
            Assert.Equal(3, handler.Requests.Count);

            // Within TTL: served from cache, no HTTP.
            var second = await resolver.ResolveAsync(TorboxStream());
            Assert.Equal(DebridResolveStatus.Success, second.Status);
            Assert.Equal(3, handler.Requests.Count);

            // After TTL: re-resolves over HTTP.
            now = now.AddMinutes(16);
            handler.Enqueue(Json(new { data = new { torrent_id = "123" } }));
            handler.Enqueue(Json(new
            {
                data = new
                {
                    data = new
                    {
                        files = new[] { new { id = "7", name = "Movie.2020.1080p.mkv", size = 1d } }
                    }
                }
            }));
            handler.Enqueue(Json(new { @data = "https://dl.torbox.com/a.mkv" }));

            var third = await resolver.ResolveAsync(TorboxStream());
            Assert.Equal(DebridResolveStatus.Success, third.Status);
            Assert.Equal(6, handler.Requests.Count);
        }

        [Fact]
        public void Resolve_CanResolveAndShouldListGates()
        {
            var handler = new RecordingHandler();
            var resolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = TorboxSettings() });

            // Cached direct debrid for the configured provider → resolvable.
            Assert.True(resolver.CanResolveStream(TorboxStream()));
            Assert.True(resolver.ShouldListStream(TorboxStream()));

            // Plain URL rows always listable but not "resolvable".
            var urlRow = new DebridStream { Url = "https://x/v.mkv" };
            Assert.False(resolver.CanResolveStream(urlRow));
            Assert.True(resolver.ShouldListStream(urlRow));

            // Torrent row without any configured provider key → not resolvable.
            var disabledResolver = new DirectDebridResolver(
                new HttpClient(handler), new StubSettings { Snapshot = new DebridSettingsSnapshot() });
            Assert.False(disabledResolver.CanResolveStream(new DebridStream { InfoHash = "abc" }));
            Assert.False(disabledResolver.ShouldListStream(new DebridStream { InfoHash = "abc" }));
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------------------------
        // Template values (js buildTemplateValues)
        // ------------------------------------------------------------------

        [Fact]
        public void TemplateValues_FullParseBuildsFlatDottedDictionary()
        {
            var stream = new DebridStream
            {
                Title = "Fallback title",
                AddonName = "Torrentio",
                DebridCacheStatus = new Models.DebridCacheStatus
                {
                    ProviderId = "torbox",
                    State = Models.DebridCacheStatus.StateCached,
                    CachedName = "cached.mkv",
                    CachedSize = 111
                },
                VideoSizeHint = 222,
                ClientResolve = new DebridClientResolve
                {
                    Type = "debrid",
                    Service = "torbox",
                    Season = 2,
                    Episode = 3,
                    Title = "Resolve title",
                    Stream = new DebridResolveStreamSource
                    {
                        Raw = new DebridResolveRawMetadata
                        {
                            Filename = "raw.mkv",
                            TorrentName = "Movie.2020.1080p",
                            Size = 4294967296,
                            FolderSize = 8589934592,
                            Indexer = "BluDV",
                            Parsed = new DebridParsedMetadata
                            {
                                ParsedTitle = "Movie",
                                Year = 2020,
                                Seasons = new List<int> { 1, 2 },
                                Episodes = new List<int> { 3, 4 },
                                Hdr = new List<string> { "HDR10_PLUS" },
                                BitDepth = "10bit",
                                Audio = new List<string> { "ATMOS" },
                                Channels = new List<string> { "CH_5_1" },
                                Languages = new List<string> { "en", "multi" },
                                Resolution = "P1080",
                                Quality = "BLURAY_REMUX",
                                Codec = "hevc",
                                Network = "AMZN",
                                Group = "GRP",
                                Duration = 5400.5,
                                Edition = "Extended"
                            }
                        }
                    }
                }
            };
            var fact = new DebridStreamFact
            {
                Resolution = "UNKNOWN",
                Quality = "WEB_DL",
                Codec = "AVC",
                ReleaseGroup = "FactGroup",
                Edition = "Theatrical",
                VisualTags = new List<string> { "HDR10" },
                AudioTags = new List<string> { "DD_PLUS" },
                AudioChannels = new List<string> { "CH_7_1" },
                Languages = new List<string> { "FR", "MULTI" }
            };

            var values = DebridTemplateValuesBuilder.Build(stream, fact);

            Assert.Equal("Movie", values["stream.title"]);
            Assert.Equal(2020L, values["stream.year"]);
            Assert.Equal(2L, values["stream.season"]);
            Assert.Equal(3L, values["stream.episode"]);
            Assert.Equal(new object[] { 1L, 2L }, values["stream.seasons"]);
            Assert.Equal(new object[] { 3L, 4L }, values["stream.episodes"]);
            Assert.Equal(new object[] { "S02E03" }, values["stream.seasonEpisode"]);
            Assert.Equal("E03 • E04", values["stream.formattedEpisodes"]);
            Assert.Equal("S01 • S02", values["stream.formattedSeasons"]);
            Assert.Equal("P1080", values["stream.resolution"]); // parsed wins over UNKNOWN fact
            Assert.Equal(false, values["stream.library"]);
            Assert.Equal("BLURAY_REMUX", values["stream.quality"]);
            Assert.Equal(new object[] { "HDR10_PLUS", "10bit" }, values["stream.visualTags"]);
            Assert.Equal(new object[] { "ATMOS" }, values["stream.audioTags"]);
            Assert.Equal(new object[] { "CH_5_1" }, values["stream.audioChannels"]);
            Assert.Equal(new object[] { "🇬🇧", "Multi" }, values["stream.languageEmojis"]);
            Assert.Equal(4294967296L, values["stream.size"]); // raw.size beats hints/cache
            Assert.Equal(8589934592L, values["stream.folderSize"]);
            Assert.Equal("HEVC", values["stream.encode"]);
            Assert.Equal("BluDV", values["stream.indexer"]);
            Assert.Equal("AMZN", values["stream.network"]);
            Assert.Equal("GRP", values["stream.releaseGroup"]);
            Assert.Equal(5400.5, values["stream.duration"]);
            Assert.Equal("Extended", values["stream.edition"]);
            Assert.Equal("raw.mkv", values["stream.filename"]);
            Assert.Null(values["stream.regexMatched"]);
            Assert.Equal("Debrid", values["stream.type"]);
            Assert.Equal(true, values["service.cached"]);
            Assert.Equal("TB", values["service.shortName"]);
            Assert.Equal("Torbox", values["service.name"]);
            Assert.Equal("Torrentio", values["addon.name"]);
            Assert.Equal(
                "Movie S02E03 • P1080",
                DebridStreamTemplateEngine.Render("{stream.title} {stream.seasonEpisode} • {stream.resolution}", values));
        }

        [Fact]
        public void TemplateValues_EmptyStreamsFallBackToFactLabelsAndNulls()
        {
            var values = DebridTemplateValuesBuilder.Build(
                new DebridStream(),
                new DebridStreamFact
                {
                    Resolution = "P2160",
                    Quality = "WEB_DL",
                    Codec = "HEVC",
                    VisualTags = new List<string> { "UNKNOWN", "HDR10" },
                    AudioTags = new List<string> { "ATMOS" },
                    AudioChannels = new List<string> { "CH_7_1" },
                    Languages = new List<string> { "JA", "XX_UNKNOWN" }
                });

            Assert.Null(values["stream.title"]);
            Assert.Null(values["stream.year"]);
            Assert.Equal("2160p", values["stream.resolution"]);
            Assert.Equal("WEB-DL", values["stream.quality"]);
            Assert.Equal("HEVC", values["stream.encode"]);
            Assert.Equal(new object[] { "HDR10" }, values["stream.visualTags"]); // UNKNOWN dropped
            Assert.Equal(new object[] { "Atmos" }, values["stream.audioTags"]);
            Assert.Equal(new object[] { "7.1" }, values["stream.audioChannels"]);
            Assert.Equal(new object[] { "ja" }, values["stream.languages"]); // unknown ids filtered
            Assert.Equal(new object[] { "🇯🇵" }, values["stream.languageEmojis"]);
            Assert.Equal("", values["stream.formattedEpisodes"]);
            Assert.Equal("", values["stream.formattedSeasons"]);
            Assert.Equal(new object[0], values["stream.seasonEpisode"]);
            Assert.Null(values["stream.size"]);
            Assert.Null(values["stream.folderSize"]);
            Assert.Null(values["stream.indexer"]);
            Assert.Null(values["stream.releaseGroup"]);
            Assert.Null(values["stream.duration"]);
            Assert.Null(values["stream.edition"]);
            Assert.Null(values["stream.filename"]);
            Assert.Equal("", values["stream.type"]);
            Assert.Null(values["service.cached"]);
            Assert.Equal("", values["service.shortName"]);
            Assert.Equal("Debrid", values["service.name"]);
            Assert.Null(values["addon.name"]);
        }

        [Fact]
        public void TemplateValues_SeasonEpisodeCrossProductAndCacheStateMapping()
        {
            var stream = new DebridStream
            {
                ClientResolve = new DebridClientResolve
                {
                    Type = "torrent",
                    IsCached = false,
                    Stream = new DebridResolveStreamSource
                    {
                        Raw = new DebridResolveRawMetadata
                        {
                            Parsed = new DebridParsedMetadata
                            {
                                Seasons = new List<int> { 1, 2 },
                                Episodes = new List<int> { 1, 2, 3 }
                            }
                        }
                    }
                }
            };

            var values = DebridTemplateValuesBuilder.Build(stream, new DebridStreamFact());

            Assert.Equal(
                new object[] { "S01E01", "S01E02", "S01E03", "S02E01", "S02E02", "S02E03" },
                values["stream.seasonEpisode"]);
            Assert.Equal("p2p", values["stream.type"]);
            Assert.Equal(false, values["service.cached"]); // resolve.isCached=false

            stream.ClientResolve.IsCached = null;
            values = DebridTemplateValuesBuilder.Build(stream, new DebridStreamFact());
            Assert.Null(values["service.cached"]);
        }
    }
}
