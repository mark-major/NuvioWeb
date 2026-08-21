using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using NuvioTV.Core.Storage;
namespace NuvioTV.Core.Media
{
    /// <summary>Envelope entry of homeImageCache.v1 (url + lastSeen ms).</summary>
    public sealed class HomeImageCacheEntry
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("lastSeen")]
        public long LastSeen { get; set; }
    }

    /// <summary>
    /// Port of js/data/local/homeImageCacheStore.js: remembers recently-shown
    /// home poster/background URLs (cap 500, 30-day TTL) over IKeyValueStore so
    /// the native home screen can warm the same set of images.
    /// </summary>
    public sealed class HomeImageCacheStore
    {
        public const string StorageKey = "homeImageCache.v1";
        public const int MaxHomeImageUrls = 500;
        public static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

        private readonly IKeyValueStore _store;
        private readonly Func<long> _nowMs;

        public HomeImageCacheStore(IKeyValueStore store, Func<long> nowMs = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>Returns up to <paramref name="limit"/> most-recently-seen URLs.</summary>
        public async Task<List<string>> GetUrlsAsync(int limit = 120)
        {
            return (await ReadEntriesAsync())
                .Take(Math.Max(0, limit))
                .Select(e => e.Url)
                .ToList();
        }

        /// <summary>Merges URLs into the cache with lastSeen=now, pruning expired and capping.</summary>
        public async Task RememberUrlsAsync(IEnumerable<string> urls)
        {
            if (urls == null) return;

            var normalizedUrls = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in urls)
            {
                var url = NormalizeUrl(raw);
                if (url.Length > 0 && seen.Add(url))
                {
                    normalizedUrls.Add(url);
                }
            }
            if (normalizedUrls.Count == 0) return;

            var now = _nowMs();
            var byUrl = new Dictionary<string, HomeImageCacheEntry>(StringComparer.Ordinal);
            foreach (var entry in await ReadEntriesAsync())
            {
                byUrl[entry.Url] = entry;
            }
            foreach (var url in normalizedUrls)
            {
                byUrl[url] = new HomeImageCacheEntry { Url = url, LastSeen = now };
            }

            await WriteEntriesAsync(byUrl.Values);
        }

        public Task ClearAsync() => _store.RemoveAsync(StorageKey);

        // ---- internals (mirror readEntries/writeEntries) ----

        private async Task<List<HomeImageCacheEntry>> ReadEntriesAsync()
        {
            var raw = await LocalStore.GetAsync<List<HomeImageCacheEntry>>(StorageKey, _store);
            var now = _nowMs();
            var cutoff = now - (long)Ttl.TotalMilliseconds;
            var result = new List<HomeImageCacheEntry>();
            if (raw == null) return result;
            foreach (var entry in raw)
            {
                var url = NormalizeUrl(entry?.Url);
                var lastSeen = entry?.LastSeen ?? 0;
                if (url.Length > 0 && lastSeen >= cutoff)
                {
                    result.Add(new HomeImageCacheEntry { Url = url, LastSeen = lastSeen });
                }
            }
            return result;
        }

        private async Task WriteEntriesAsync(IEnumerable<HomeImageCacheEntry> entries)
        {
            var capped = entries
                .Where(e => !string.IsNullOrEmpty(e?.Url))
                .OrderByDescending(e => e.LastSeen)
                .Take(MaxHomeImageUrls)
                .ToList();
            await LocalStore.SetAsync(StorageKey, capped, _store);
        }

        internal static string NormalizeUrl(string value)
        {
            var url = (value ?? "").Trim();
            if (url.Length == 0 || url.StartsWith("data:", StringComparison.Ordinal) ||
                url.StartsWith("blob:", StringComparison.Ordinal))
            {
                return "";
            }
            return url;
        }
    }
}
