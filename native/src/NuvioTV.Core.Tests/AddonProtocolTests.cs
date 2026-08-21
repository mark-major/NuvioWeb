using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using Xunit;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the Stremio addon protocol layer against the JS builders:
    /// addonRepository.js, catalogRepository.js, metaRepository.js,
    /// streamRepository.js, subtitleRepository.js.
    /// </summary>
    public class AddonProtocolTests
    {
        // ------------------------------------------------------------------
        // Canonicalization
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("https://addon.example.com/manifest.json", "https://addon.example.com")]
        [InlineData("https://addon.example.com/manifest.json?token=abc", "https://addon.example.com?token=abc")]
        [InlineData("https://addon.example.com/", "https://addon.example.com")]
        [InlineData("https://ADDON.example.COM/MANIFEST.JSON", "https://ADDON.example.COM")]
        [InlineData("https://addon.example.com", "https://addon.example.com")]
        public void CanonicalizeUrl_StripsManifestSuffix_PreservesQuery(string input, string expected)
        {
            Assert.Equal(expected, AddonUrlBuilder.CanonicalizeUrl(input));
        }

        [Fact]
        public void BuildManifestUrl_AppendsSuffix_AfterCleanBase()
        {
            Assert.Equal("https://addon.example.com/manifest.json",
                AddonUrlBuilder.BuildManifestUrl("https://addon.example.com/manifest.json"));
            Assert.Equal("https://addon.example.com/manifest.json?token=t",
                AddonUrlBuilder.BuildManifestUrl("https://addon.example.com/manifest.json?token=t"));
        }

        // ------------------------------------------------------------------
        // Resource URL shapes
        // ------------------------------------------------------------------

        [Fact]
        public void CatalogUrl_Plain_AndSkipForms()
        {
            var baseUrl = "https://v3-cinemeta.strem.io";
            Assert.Equal("https://v3-cinemeta.strem.io/catalog/movie/top.json",
                AddonUrlBuilder.BuildCatalogUrl(baseUrl, "movie", "top"));
            Assert.Equal("https://v3-cinemeta.strem.io/catalog/movie/top/skip=100.json",
                AddonUrlBuilder.BuildCatalogUrl(baseUrl, "movie", "top", skip: 100));
        }

        [Fact]
        public void CatalogUrl_WithExtraArgs_MovesSkipIntoQuerySegment()
        {
            var url = AddonUrlBuilder.BuildCatalogUrl(
                "https://addon.example.com", "series", "top", skip: 50,
                extraArgs: new Dictionary<string, string> { ["genre"] = "Action & Adventure" });

            Assert.Equal("https://addon.example.com/catalog/series/top/genre=Action%20%26%20Adventure&skip=50.json", url);
        }

        [Fact]
        public void CatalogUrl_BaseQueryPreservedVerbatim()
        {
            var url = AddonUrlBuilder.BuildCatalogUrl(
                "https://addon.example.com/manifest.json?token=xyz", "movie", "top");

            Assert.Equal("https://addon.example.com/catalog/movie/top.json?token=xyz", url);
        }

        [Theory]
        [InlineData("tt1254207", "https://a.io/meta/tt1254207.json")] // placeholder replaced below
        public void MetaUrl_EncodesTypeAndId(string id, string _)
        {
            var url = AddonUrlBuilder.BuildMetaUrl("https://a.io", "series", id);
            Assert.Equal("https://a.io/meta/series/tt1254207.json", url);
        }

        [Fact]
        public void MetaUrl_SpaceBecomesPercent20()
        {
            var url = AddonUrlBuilder.BuildMetaUrl("https://a.io", "movie", "some id");
            Assert.Equal("https://a.io/meta/movie/some%20id.json", url);
        }

        [Fact]
        public void StreamUrl_Shape()
        {
            var url = AddonUrlBuilder.BuildStreamUrl("https://a.io", "series", "tt1:1:2");
            // JS encode() = encodeURIComponent → ':' becomes %3A in stream ids too.
            Assert.Equal("https://a.io/stream/series/tt1%3A1%3A2.json", url);
        }

        [Fact]
        public void SubtitlesUrl_PlainAndExtraParams()
        {
            Assert.Equal("https://a.io/subtitles/series/tt1:1:2.json",
                AddonUrlBuilder.BuildSubtitlesUrl("https://a.io", "series", "tt1:1:2"));

            var withExtras = AddonUrlBuilder.BuildSubtitlesUrl(
                "https://a.io", "movie", "tt2",
                videoHash: "9c8e7f", videoSize: 123456789, filename: "My Movie.mp4");

            Assert.Equal(
                "https://a.io/subtitles/movie/tt2/videoHash=9c8e7f&videoSize=123456789&filename=My%20Movie.mp4.json",
                withExtras);
        }

        [Fact]
        public void SubtitlesUrl_EmptyExtrasOmitted_ZeroSizeSkipped()
        {
            var url = AddonUrlBuilder.BuildSubtitlesUrl(
                "https://a.io", "movie", "tt3", videoHash: "", videoSize: 0, filename: null);

            Assert.Equal("https://a.io/subtitles/movie/tt3.json", url);
        }

        // ------------------------------------------------------------------
        // Cinemeta normalization + fallback manifest
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("http://cinemeta-v3.strem.io/manifest.json", "https://v3-cinemeta.strem.io/manifest.json")]
        [InlineData("HTTPS://CINEMETA-V3.STREM.IO", "https://v3-cinemeta.strem.io")]
        [InlineData("https://other.addon.io", "https://other.addon.io")]
        public void NormalizeCinemetaUrl_MigratesLegacyHost(string input, string expected)
        {
            Assert.Equal(expected, AddonCanonicalizer.NormalizeCinemetaUrl(input));
        }

        [Fact]
        public void FallbackManifest_OnlyForCinemeta()
        {
            Assert.Null(AddonCanonicalizer.GetBuiltinFallbackManifest("https://other.addon.io"));

            var manifest = AddonCanonicalizer.GetBuiltinFallbackManifest("https://v3-cinemeta.strem.io/manifest.json");
            Assert.NotNull(manifest);
            Assert.Equal("org.cinemeta", manifest.Id);
            Assert.Equal("fallback", manifest.Version);
            Assert.Equal(2, manifest.Resources.Count);
            Assert.Equal(2, manifest.Catalogs.Count);
            Assert.Equal("movie", manifest.Catalogs[0].Type);
        }

        // ------------------------------------------------------------------
        // HTTP client behavior
        // ------------------------------------------------------------------

        [Fact]
        public async Task FetchManifestAsync_HitsManifestUrl_WithoutSessionAuth()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { id = "org.test", name = "Test" }));
            var client = new StremioAddonClient(CreateHttp(handler));

            var manifest = await client.FetchManifestAsync("https://addon.example.com");

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://addon.example.com/manifest.json", request.RequestUri.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("org.test", manifest.Id);
        }

        [Fact]
        public async Task FetchCatalogAsync_DeserializesMetasArray()
        {
            var handler = new RecordingHandler();
            handler.Enqueue(JsonResponse(new { metas = new[] { new { id = "tt1", name = "One" } } }));
            var client = new StremioAddonClient(CreateHttp(handler));

            var payload = await client.FetchCatalogAsync("https://a.io", "movie", "top");

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://a.io/catalog/movie/top.json", request.RequestUri.ToString());
            Assert.Equal(JsonValueKind.Object, payload.ValueKind);
            Assert.Equal(JsonValueKind.Array, payload.GetProperty("metas").ValueKind);
        }

        private static NuvioHttpClient CreateHttp(RecordingHandler handler)
        {
            return new NuvioHttpClient(new HttpClient(handler), new NullSessionTokens());
        }

        private static HttpResponseMessage JsonResponse(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }

        private sealed class NullSessionTokens : ISessionTokenProvider
        {
            public string GetAccessToken() => null;
            public string GetRefreshToken() => null;
            public bool HasTokens() => false;
            public Task<bool> TryRefreshAsync(bool force = false) => Task.FromResult(false);
        }
    }
}
