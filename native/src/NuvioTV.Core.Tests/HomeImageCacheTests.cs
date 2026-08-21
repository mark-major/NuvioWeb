using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Media;
using NuvioTV.Core.Storage;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>Golden tests for the homeImageCache.v1 port (homeImageCacheStore.js).</summary>
    public class HomeImageCacheStoreTests
    {
        private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        [Fact]
        public async Task RememberUrls_DedupesAndOrdersByRecency()
        {
            var store = new InMemoryKeyValueStore();
            long clock = NowMs;
            var cache = new HomeImageCacheStore(store, () => clock);

            await cache.RememberUrlsAsync(new[] { "http://a/1.jpg", "http://a/2.jpg" });
            clock += 1000;
            await cache.RememberUrlsAsync(new[] { "http://a/1.jpg", "http://a/3.jpg" });

            // 1 and 3 were seen most recently; 2 keeps its older stamp.
            Assert.Equal(new[] { "http://a/1.jpg", "http://a/3.jpg", "http://a/2.jpg" },
                await cache.GetUrlsAsync(10));
        }

        [Theory]
        [InlineData("data:image/png;base64,AAA")]
        [InlineData("blob:http://x/y")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        public async Task RememberUrls_SkipsInvalid(string bad)
        {
            var store = new InMemoryKeyValueStore();
            var cache = new HomeImageCacheStore(store);

            await cache.RememberUrlsAsync(new[] { bad, "http://ok/1.jpg" });

            Assert.Equal(new[] { "http://ok/1.jpg" }, await cache.GetUrlsAsync(10));
        }

        [Fact]
        public async Task GetUrls_RespectsLimit()
        {
            var store = new InMemoryKeyValueStore();
            var cache = new HomeImageCacheStore(store);
            var urls = Enumerable.Range(0, 20).Select(i => $"http://a/{i}.jpg");
            await cache.RememberUrlsAsync(urls);

            Assert.Equal(5, (await cache.GetUrlsAsync(5)).Count);
            Assert.Equal(20, (await cache.GetUrlsAsync()).Count); // default limit 120
        }

        [Fact]
        public async Task Entries_ExpireAfter30Days_AndCapAt500()
        {
            long now = NowMs;
            long clock = now;
            var store = new InMemoryKeyValueStore();
            var cache = new HomeImageCacheStore(store, () => clock);

            // Seed one entry that will be stale.
            clock = now - (long)TimeSpan.FromDays(31).TotalMilliseconds;
            await cache.RememberUrlsAsync(new[] { "http://old/1.jpg" });

            // Fresh entries beyond the cap.
            clock = now;
            var many = Enumerable.Range(0, 520).Select(i => $"http://many/{i}.jpg");
            await cache.RememberUrlsAsync(many);

            var urls = await cache.GetUrlsAsync(1000);
            Assert.DoesNotContain("http://old/1.jpg", urls);
            Assert.Equal(HomeImageCacheStore.MaxHomeImageUrls, urls.Count);
        }

        [Fact]
        public async Task Envelope_MatchesWebappShape()
        {
            var store = new InMemoryKeyValueStore();
            var cache = new HomeImageCacheStore(store);
            await cache.RememberUrlsAsync(new[] { "http://a/1.jpg" });

            var raw = await store.GetAsync(HomeImageCacheStore.StorageKey);
            Assert.Contains("\"url\":\"http://a/1.jpg\"", raw);
            Assert.Contains("\"lastSeen\":", raw);
        }
    }
}
