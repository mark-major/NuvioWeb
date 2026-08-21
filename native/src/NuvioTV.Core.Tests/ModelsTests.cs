using System;
using System.Collections.Generic;
using System.Text.Json;
using NuvioTV.Core.Models;
using Xunit;

namespace NuvioTV.Core.Tests
{
    public class ModelsTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        [Fact]
        public void Addon_SerializesCorrectly()
        {
            var addon = new Addon(
                "test-addon",
                "Test Addon",
                "Test Addon Display",
                "1.0.0",
                "",
                "",
                "https://example.com",
                new List<AddonCatalog> { new AddonCatalog { Id = "test-catalog", Name = "Test Catalog", Type = "test-type", ApiType = "test-api" } },
                new List<string> { "movie", "series" },
                new List<string> { "movie", "series" },
                new List<AddonResource> { new AddonResource { Name = "catalog", Types = new List<string> { "movie", "series" } } }
            );

            var json = JsonSerializer.Serialize(addon, JsonOptions);
            var expected = @"{""id"":""test-addon"",""name"":""Test Addon"",""displayName"":""Test Addon Display"",""version"":""1.0.0"",""description"":"""",""logo"":"""",""baseUrl"":""https://example.com"",""catalogs"":[{""id"":""test-catalog"",""name"":""Test Catalog"",""type"":""test-type"",""apiType"":""test-api"",""extra"":[]}],""types"":[""movie"",""series""],""rawTypes"":[""movie"",""series""],""resources"":[{""name"":""catalog"",""types"":[""movie"",""series""],""idPrefixes"":[]}]}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void Meta_SerializesCorrectly()
        {
            var meta = new Meta(
                "tmu123",
                "movie",
                "Test Movie",
                "https://example.com/poster.jpg",
                "https://example.com/back.jpg",
                "https://example.com/logo.png",
                "Test description",
                new List<string> { "Action", "Drama" },
                new List<MetaVideo> { new MetaVideo { Id = "1", Title = "Episode 1", Season = 1, Episode = 1 } },
                "2024"
            );

            var json = JsonSerializer.Serialize(meta, JsonOptions);
            var expected = @"{""id"":""tmu123"",""type"":""movie"",""name"":""Test Movie"",""poster"":""https://example.com/poster.jpg"",""background"":""https://example.com/back.jpg"",""logo"":""https://example.com/logo.png"",""description"":""Test description"",""genres"":[""Action"",""Drama""],""videos"":[{""id"":""1"",""title"":""Episode 1"",""season"":1,""episode"":1}],""releaseInfo"":""2024""}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void StreamItem_SerializesCorrectly()
        {
            var stream = new StreamItem
            {
                Name = "Test Stream",
                Title = "Test Title",
                Url = "https://example.com/stream.mp4",
                YtId = "yt123",
                InfoHash = "abc123",
                FileIdx = null,
                ExternalUrl = "https://example.com/external",
                BehaviorHints = new StreamBehaviorHints { IsProxy = true },
                AddonName = "Test Addon",
                AddonLogo = "https://example.com/addonlogo.png",
                ClientResolve = new ClientResolve
                {
                    Type = "torrent",
                    Service = "real-debrid",
                    InfoHash = "abc123",
                    FileIdx = 0,
                    Filename = "test.mkv",
                    TorrentName = "Test Torrent",
                    MagnetUri = "magnet:?xt=abc123",
                    Sources = new List<string> { "tracker1", "tracker2" }
                },
                DebridCacheStatus = new DebridCacheStatus
                {
                    ProviderId = "real-debrid",
                    ProviderName = "Real-Debrid",
                    State = "CACHED",
                    CachedName = "test.mkv",
                    CachedSize = 1073741824
                }
            };

            var json = JsonSerializer.Serialize(stream, JsonOptions);
            Console.WriteLine("Actual JSON: " + json);
            var expected = @"{""name"":""Test Stream"",""title"":""Test Title"",""url"":""https://example.com/stream.mp4"",""ytId"":""yt123"",""infoHash"":""abc123"",""fileIdx"":null,""externalUrl"":""https://example.com/external"",""behaviorHints"":{""isProxy"":true,""notWebReady"":false},""addonName"":""Test Addon"",""addonLogo"":""https://example.com/addonlogo.png"",""subtitles"":[],""sources"":[],""qualityValue"":0,""clientResolve"":{""type"":""torrent"",""service"":""real-debrid"",""infoHash"":""abc123"",""fileIdx"":0,""filename"":""test.mkv"",""torrentName"":""Test Torrent"",""magnetUri"":""magnet:?xt=abc123"",""sources"":[""tracker1"",""tracker2""]},""debridCacheStatus"":{""providerId"":""real-debrid"",""providerName"":""Real-Debrid"",""state"":""CACHED"",""cachedName"":""test.mkv"",""cachedSize"":1073741824}}";
            Console.WriteLine("Expected JSON: " + expected);
            Assert.Equal(expected, json);
        }

        [Fact]
        public void SubtitleItem_SerializesCorrectly()
        {
            var subtitle = new SubtitleItem(
                "sub1",
                "https://example.com/sub.srt",
                "en",
                "Test Addon",
                "https://example.com/addonlogo.png"
            );

            var json = JsonSerializer.Serialize(subtitle, JsonOptions);
            var expected = @"{""id"":""sub1"",""url"":""https://example.com/sub.srt"",""lang"":""en"",""addonName"":""Test Addon"",""addonLogo"":""https://example.com/addonlogo.png""}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void CatalogRow_SerializesCorrectly()
        {
            var catalog = new CatalogRow(
                "addon1",
                "Test Addon",
                "https://example.com",
                "cat1",
                "Test Catalog",
                "test-api",
                new List<Meta>(),
                false,
                false,
                0,
                true
            );

            var json = JsonSerializer.Serialize(catalog, JsonOptions);
            var expected = @"{""addonId"":""addon1"",""addonName"":""Test Addon"",""addonBaseUrl"":""https://example.com"",""catalogId"":""cat1"",""catalogName"":""Test Catalog"",""apiType"":""test-api"",""items"":[],""isLoading"":false,""hasMore"":false,""currentPage"":0,""supportsSkip"":true}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void UserProfile_SerializesCorrectly()
        {
            var profile = new UserProfile(
                "1",
                1,
                "Profile 1",
                "#1E88E5",
                true,
                true,
                false,
                "avatar1",
                "https://example.com/avatar.jpg"
            );

            var json = JsonSerializer.Serialize(profile, JsonOptions);
            var expected = @"{""id"":""1"",""profileIndex"":1,""name"":""Profile 1"",""avatarColorHex"":""#1E88E5"",""isPrimary"":true,""usesPrimaryAddons"":true,""usesPrimaryPlugins"":false,""avatarId"":""avatar1"",""avatarUrl"":""https://example.com/avatar.jpg""}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void WatchProgress_SerializesCorrectly()
        {
            var progress = new WatchProgress(
                "tmu123",
                "movie",
                "1",
                60000,
                7200000,
                1692519487000
            );

            var json = JsonSerializer.Serialize(progress, JsonOptions);
            var expected = @"{""contentId"":""tmu123"",""contentType"":""movie"",""videoId"":""1"",""positionMs"":60000,""durationMs"":7200000,""updatedAt"":1692519487000}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void ContentTypes_ConstantsAreCorrect()
        {
            Assert.Equal("movie", ContentTypes.Movie);
            Assert.Equal("series", ContentTypes.Series);
            Assert.Equal("tv", ContentTypes.Tv);
            Assert.Equal("channel", ContentTypes.Channel);
            Assert.Equal("anime", ContentTypes.Anime);
        }

        [Fact]
        public void WatchProgress_ThresholdsAreCorrect()
        {
            Assert.Equal(0.02, WatchProgress.StartedThreshold);
            Assert.Equal(0.90, WatchProgress.CompletedThreshold);
        }

        [Fact]
        public void LibraryEntry_SerializesCorrectly()
        {
            var entry = new LibraryEntry(
                "tmu123",
                "movie",
                "Test Movie",
                "https://example.com/poster.jpg",
                "https://example.com/back.jpg",
                "Test description",
                "2024",
                7.5,
                new List<string> { "Action", "Drama" },
                "https://example.com",
                new List<string> { "watchlist", "favorites" },
                1692519487000,
                10,
                new Dictionary<string, LibraryEntryListMeta>
                {
                    ["watchlist"] = new LibraryEntryListMeta(1692519487000, 5)
                }
            );

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var expected = @"{""id"":""tmu123"",""type"":""movie"",""name"":""Test Movie"",""poster"":""https://example.com/poster.jpg"",""background"":""https://example.com/back.jpg"",""description"":""Test description"",""releaseInfo"":""2024"",""imdbRating"":7.5,""genres"":[""Action"",""Drama""],""addonBaseUrl"":""https://example.com"",""listKeys"":[""watchlist"",""favorites""],""listedAt"":1692519487000,""traktRank"":10,""listMeta"":{""watchlist"":{""listedAt"":1692519487000,""traktRank"":5}}}";
            Assert.Equal(expected, json);
        }

        [Fact]
        public void ProfileEnvelope_SerializesCorrectly()
        {
            var envelope = new ProfileEnvelope(
                1,
                new Dictionary<string, object>
                {
                    ["1"] = new { value = "test1" },
                    ["2"] = new { value = "test2" }
                }
            );

            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            var expected = @"{""__profileScoped"":true,""version"":1,""profiles"":{""1"":{""value"":""test1""},""2"":{""value"":""test2""}}}";
            Assert.Equal(expected, json);
        }
    }
}
