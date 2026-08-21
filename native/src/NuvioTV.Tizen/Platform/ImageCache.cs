using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Media;
using NuvioTV.Core.Storage;

namespace NuvioTV.Tizen.Platform
{
    /// <summary>
    /// Disk-backed LRU image cache. Metadata envelope mirrors
    /// homeImageCache.v1 (cap 500 / 30-day TTL) persisted through LocalStore so
    /// tooling sees the same key; payloads are files under &lt;data&gt;/image-cache/.
    /// Also backs the addon-logo cache (nuvio.stream.addonLogoCache.v1) as files.
    /// </summary>
    public sealed class ImageCache : IDisposable
    {
        public const string AddonLogoStorageKey = "nuvio.stream.addonLogoCache.v1";
        private const int AddonLogoCap = 36;

        private readonly IKeyValueStore _store;
        private readonly HomeImageCacheStore _homeIndex;
        private readonly HttpClient _http;
        private readonly string _cacheDir;
        private readonly SemaphoreSlim _downloadGate = new SemaphoreSlim(4, 4);
        private readonly Dictionary<string, Task<string>> _inFlight =
            new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public ImageCache(IKeyValueStore store, HttpClient http, string dataDir)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            if (string.IsNullOrEmpty(dataDir)) throw new ArgumentException("dataDir required", nameof(dataDir));
            _cacheDir = Path.Combine(dataDir, "image-cache");
            Directory.CreateDirectory(_cacheDir);
            _homeIndex = new HomeImageCacheStore(store);
        }

        /// <summary>Returns a local file path for the URL, downloading when missing/expired; null on failure.</summary>
        public Task<string> GetAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.FromResult<string>(null);
            }
            lock (_gate)
            {
                if (_inFlight.TryGetValue(url, out var running))
                {
                    return running;
                }
                var task = Task.Run(() => GetCoreAsync(url, ct), ct);
                _inFlight[url] = task;
                task.ContinueWith(_ =>
                {
                    lock (_gate) { _inFlight.Remove(url); }
                });
                return task;
            }
        }

        private async Task<string> GetCoreAsync(string url, CancellationToken ct)
        {
            var path = PathFor(url);
            try
            {
                if (File.Exists(path))
                {
                    await TouchHomeIndexAsync(url);
                    return path;
                }

                await _downloadGate.WaitAsync(ct);
                try
                {
                    using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        if (!response.IsSuccessStatusCode || response.Content == null)
                        {
                            return null;
                        }
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        if (bytes.Length == 0) return null;

                        // Atomic write: tmp + rename (JsonFileStore convention).
                        var tmp = path + ".tmp";
                        File.WriteAllBytes(tmp, bytes);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(tmp, path);
                    }
                    await TouchHomeIndexAsync(url);
                    await PruneAsync();
                    return path;
                }
                finally
                {
                    _downloadGate.Release();
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                global::Tizen.Log.Warn("NuvioTV", $"image cache miss {url}: {ex.Message}");
                TryDelete(path);
                return null;
            }
        }

        /// <summary>URL → deterministic file name (SHA-256 of the URL, extension preserved).</summary>
        public string PathFor(string url)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
            }
            var name = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            var ext = ".img";
            var queryIdx = (url ?? "").LastIndexOf('.');
            if (queryIdx > 0 && queryIdx > url.LastIndexOf('/'))
            {
                var candidate = url.Substring(queryIdx);
                if (candidate.Length <= 5 && candidate.IndexOf('?') < 0 && candidate.All(char.IsLetterOrDigit))
                {
                    ext = candidate.ToLowerInvariant();
                }
            }
            return Path.Combine(_cacheDir, name + ext);
        }

        /// <summary>Prunes the file cache to the home-index cap and TTL.</summary>
        private async Task PruneAsync()
        {
            List<string> keepUrls;
            try
            {
                keepUrls = await _homeIndex.GetUrlsAsync(HomeImageCacheStore.MaxHomeImageUrls);
            }
            catch
            {
                return;
            }
            var keep = new HashSet<string>(keepUrls.Select(PathFor), StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(_cacheDir))
            {
                if (!keep.Contains(file))
                {
                    TryDelete(file);
                }
            }
        }

        /// <summary>Records the URL in the homeImageCache.v1 index (TTL/cap enforcement).</summary>
        private async Task TouchHomeIndexAsync(string url)
        {
            try
            {
                await _homeIndex.RememberUrlsAsync(new[] { url });
            }
            catch
            {
                // Index bookkeeping is best-effort.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }

        public void Dispose()
        {
            _downloadGate?.Dispose();
        }
    }
}
