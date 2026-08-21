using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using NuvioTV.Core.Models;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for AddonRepository against js/data/repository/addonRepository.js:
    /// install/persist/list, enabled-state filtering, builtin cinemeta fallback,
    /// concurrent manifest dedupe, display-name overrides, resource type recovery.
    /// </summary>
    public class AddonRepositoryTests
    {
        private static ManifestFetchOutcome StubManifest(
            string url, string id = null, string name = null, int delayMs = 0)
        {
            return new ManifestFetchOutcome { Url = url, Id = id, Name = name, DelayMs = delayMs };
        }

        public sealed class ManifestFetchOutcome
        {
            public string Url { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public int DelayMs { get; set; }
            public bool Throw { get; set; }

            public AddonManifest ToManifest()
            {
                var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(Url);
                return new AddonManifest
                {
                    Id = Id ?? ("org.test." + cleanUrl.TrimEnd('/').Split('/').Last()),
                    Name = Name ?? "Test Addon",
                    Version = "1.0.0",
                    Types = new[] { "movie", "series" },
                    Resources = new AddonManifestResource[]
                    {
                        new AddonManifestResource { Name = "catalog", Types = new[] { "movie" } },
                        new AddonManifestResource { Name = "meta", Types = new[] { "movie" } }
                    },
                    Catalogs = Array.Empty<AddonManifestCatalog>()
                };
            }
        }

        /// <summary>Counting fetcher seam: keyed stubs + default behavior.</summary>
        private sealed class FetchCounter
        {
            public int Calls { get; private set; }
            private readonly Dictionary<string, ManifestFetchOutcome> _stubs =
                new Dictionary<string, ManifestFetchOutcome>();

            public void Stub(string url, ManifestFetchOutcome outcome)
            {
                _stubs[AddonUrlBuilder.BuildManifestUrl(AddonUrlBuilder.CanonicalizeUrl(url))] = outcome;
            }

            public async Task<AddonManifest> FetchAsync(string manifestUrl, CancellationToken ct)
            {
                Calls++;
                if (_stubs.TryGetValue(manifestUrl, out var outcome))
                {
                    if (outcome.DelayMs > 0)
                    {
                        await Task.Delay(outcome.DelayMs, ct).ConfigureAwait(false);
                    }
                    if (outcome.Throw)
                    {
                        throw new InvalidOperationException("manifest unavailable");
                    }
                    return outcome.ToManifest();
                }
                throw new InvalidOperationException("manifest unavailable");
            }
        }

        private static AddonRepository CreateRepo(
            MemoryKeyValueStore store, FetchCounter counter)
        {
            return new AddonRepository(store, manifestFetcher: counter.FetchAsync);
        }

        // ------------------------------------------------------------------
        // Install → persist → list (brief step 1a)
        // ------------------------------------------------------------------

        [Fact]
        public async Task AddAddon_PersistsCanonicalUrlAndLists()
        {
            var store = new MemoryKeyValueStore();
            var counter = new FetchCounter();
            counter.Stub("https://example.com",
                StubManifest("https://example.com/manifest.json", id: "org.example"));
            var repo = CreateRepo(store, counter);

            var added = await repo.AddAddonAsync("https://example.com/manifest.json");
            Assert.True(added);

            // Persisted under the profile-scoped envelope key.
            var raw = await store.GetAsync("installedAddonUrls");
            Assert.Contains("https://example.com\"", raw);
            Assert.Contains("__profileScoped", raw);

            var urls = await repo.GetInstalledAddonUrlsAsync();
            Assert.Equal(
                new[] { "https://v3-cinemeta.strem.io", "https://opensubtitles-v3.strem.io",
                        "https://example.com" },
                urls);

            // Re-adding the same canonical URL is a no-op.
            Assert.False(await repo.AddAddonAsync("https://example.com/manifest.json"));
            Assert.Equal(3, (await repo.GetInstalledAddonUrlsAsync()).Count);

            // Installed addons list resolves manifests (cinemeta fallback +
            // opensubtitles error + example success = 3 fetch attempts).
            var installed = await repo.GetInstalledAddonsAsync();
            Assert.Contains(installed, addon => addon.Id == "org.example");
            Assert.Equal(3, counter.Calls);
        }

        [Fact]
        public async Task FreshStore_DefaultsToBuiltinAddonUrls()
        {
            var repo = CreateRepo(new MemoryKeyValueStore(), new FetchCounter());

            var urls = await repo.GetInstalledAddonUrlsAsync();

            Assert.Equal(
                new[] { "https://v3-cinemeta.strem.io", "https://opensubtitles-v3.strem.io" },
                urls);
        }

        // ------------------------------------------------------------------
        // Enabled states (brief step 1b)
        // ------------------------------------------------------------------

        [Fact]
        public async Task DisabledAddon_ExcludedFromList_IncludedWithFlag()
        {
            var store = new MemoryKeyValueStore();
            var counter = new FetchCounter();
            counter.Stub("https://example.com", StubManifest("https://example.com", id: "org.ex"));
            var repo = CreateRepo(store, counter);
            await repo.AddAddonAsync("https://example.com");

            Assert.True(await repo.SetEnabledAsync("https://example.com", false));

            var enabled = await repo.GetInstalledAddonsAsync();
            Assert.DoesNotContain(enabled, addon => addon.Id == "org.ex");

            var all = await repo.GetInstalledAddonsAsync(includeDisabled: true);
            Assert.Contains(all, addon => addon.Id == "org.ex");
            Assert.True(await repo.IsAddonEnabledAsync("https://example.com") == false);
        }

        // ------------------------------------------------------------------
        // Builtin fallback (brief step 1c)
        // ------------------------------------------------------------------

        [Fact]
        public async Task CinemetaFetchFailure_FallsBackToBuiltinManifest()
        {
            var counter = new FetchCounter(); // no stubs → everything throws
            var repo = CreateRepo(new MemoryKeyValueStore(), counter);

            var result = await repo.FetchAddonAsync("https://v3-cinemeta.strem.io");

            Assert.Equal("success", result.Status);
            Assert.Equal("org.cinemeta", result.Data.Id);
            Assert.Equal("Cinemeta", result.Data.Name);
            Assert.Equal("fallback", result.Data.Version);
            Assert.Contains(result.Data.Catalogs, catalog =>
                catalog.Id == "top" && catalog.ApiType == "series");
            // Fallback is cached for subsequent calls without refetching.
            var callsAfterFirst = counter.Calls;
            var second = await repo.FetchAddonAsync("https://v3-cinemeta.strem.io", preferCache: true);
            Assert.Equal("success", second.Status);
            Assert.Equal(callsAfterFirst, counter.Calls);
        }

        [Fact]
        public async Task UnknownAddonFetchFailure_ReturnsError()
        {
            var counter = new FetchCounter();
            var repo = CreateRepo(new MemoryKeyValueStore(), counter);

            var result = await repo.FetchAddonAsync("https://ghost.example");

            Assert.Equal("error", result.Status);
            Assert.NotNull(result.Message);
        }

        // ------------------------------------------------------------------
        // In-flight dedupe (brief step 1d)
        // ------------------------------------------------------------------

        [Fact]
        public async Task ConcurrentManifestFetches_DedupeIntoSingleCall()
        {
            var counter = new FetchCounter();
            counter.Stub("https://slow.example",
                StubManifest("https://slow.example", delayMs: 150));
            var repo = CreateRepo(new MemoryKeyValueStore(), counter);

            var first = repo.FetchAddonAsync("https://slow.example");
            var second = repo.FetchAddonAsync("https://slow.example");
            var results = await Task.WhenAll(first, second);

            Assert.All(results, result => Assert.Equal("success", result.Status));
            Assert.Equal(1, counter.Calls);
        }

        // ------------------------------------------------------------------
        // Remove / order / events
        // ------------------------------------------------------------------

        [Fact]
        public async Task RemoveAddon_PersistsAndFiresEvent()
        {
            var store = new MemoryKeyValueStore();
            var counter = new FetchCounter();
            counter.Stub("https://example.com", StubManifest("https://example.com"));
            var repo = CreateRepo(store, counter);
            await repo.AddAddonAsync("https://example.com");

            var reasons = new List<string>();
            repo.OnInstalledAddonsChanged += reasons.Add;

            Assert.True(await repo.RemoveAddonAsync("https://example.com"));
            Assert.False(await repo.RemoveAddonAsync("https://example.com"));

            Assert.Equal(new[] { "remove" }, reasons);
            Assert.DoesNotContain("example.com",
                await store.GetAsync("installedAddonUrls"));
        }
        [Fact]
        public async Task SetAddonOrder_ReordersAndDropsUnknowns()
        {
            var store = new MemoryKeyValueStore();
            var repo = CreateRepo(store, new FetchCounter());
            await repo.AddAddonAsync("https://a.example");
            await repo.AddAddonAsync("https://b.example");

            var changed = await repo.SetAddonOrderAsync(new[]
            {
                "https://b.example", "https://a.example"
            });

            Assert.True(changed);
            Assert.Equal(
                new[] { "https://b.example", "https://a.example" },
                await repo.GetInstalledAddonUrlsAsync());

            // Dropping b removes it entirely.
            await repo.SetAddonOrderAsync(new[] { "https://a.example" });
            Assert.Equal(
                new[] { "https://a.example" },
                await repo.GetInstalledAddonUrlsAsync());
        }

        // ------------------------------------------------------------------
        // Display names (js applyDisplayNames)
        // ------------------------------------------------------------------

        [Fact]
        public async Task DisplayNameOverrides_ApplyAndSuffixDuplicates()
        {
            var counter = new FetchCounter();
            counter.Stub("https://one.example", StubManifest("https://one.example", name: "Same"));
            counter.Stub("https://two.example", StubManifest("https://two.example", name: "Same"));
            counter.Stub("https://three.example",
                StubManifest("https://three.example", name: "Same"));
            var store = new MemoryKeyValueStore();
            var repo = CreateRepo(store, counter);
            await repo.AddAddonAsync("https://one.example");
            await repo.AddAddonAsync("https://two.example");
            await repo.AddAddonAsync("https://three.example");

            // Override one addon's display name.
            Assert.True(await repo.SetDisplayNameAsync("https://one.example", "Renamed"));

            var installed = await repo.GetInstalledAddonsAsync(includeDisabled: true);
            var names = installed.Select(addon => addon.DisplayName).ToList();

            Assert.Contains("Renamed", names);
            // Unrenamed duplicates get "(2)"/"(3)" suffixes in install order.
            var suffixes = names.Where(name => name.StartsWith("Same")).ToList();
            // js applyDisplayNames: first unrenamed duplicate keeps the plain
            // name, subsequent ones get "(2)", "(3)", ...
            Assert.Equal(new[] { "Same", "Same (2)" }, suffixes);

            // Clearing the override restores duplicate suffixing. Cinemeta's
            // builtin fallback (default addon) also appears in the list.
            await repo.SetDisplayNameAsync("https://one.example", "");
            var reloaded = await repo.GetInstalledAddonsAsync(force: true, includeDisabled: true);
            Assert.Contains("Cinemeta", reloaded.Select(addon => addon.DisplayName));
            Assert.Equal(new[] { "Same", "Same (2)", "Same (3)" },
                reloaded.Select(addon => addon.DisplayName)
                    .Where(name => name.StartsWith("Same")));
        }

        // ------------------------------------------------------------------
        // Resource matching + type recovery (js resolveResourceRequestType)
        // ------------------------------------------------------------------

        private static Models.Addon AddonWithResources(params AddonResource[] resources)
        {
            return new Models.Addon(
                "org.r", "R", "R", "1.0.0", null, null, "https://r.example",
                Array.Empty<AddonCatalog>(),
                new[] { "movie" }, new[] { "movie" }, new[] { "tt" }, resources);
        }

        [Fact]
        public void ResolveResourceRequestType_MatchesNamePrefixAndType()
        {
            var addon = AddonWithResources(
                new AddonResource { Name = "catalog", Types = new[] { "series" } });

            // No ID prefixes on the resource → type accepted as requested.
            Assert.Equal("series", AddonRepository.ResolveResourceRequestType(
                addon, "catalog", "series", "tt123"));
            // Different resource name → no match.
            Assert.Equal("", AddonRepository.ResolveResourceRequestType(
                addon, "meta", "series", "tt123"));
        }

        [Fact]
        public void ResolveResourceRequestType_PrefixMismatchBlocks()
        {
            var addon = AddonWithResources(
                new AddonResource
                {
                    Name = "meta",
                    Types = new[] { "movie" },
                    IdPrefixes = new[] { "tt" }
                });

            // Requested type matches but ID prefix does not.
            Assert.Equal("", AddonRepository.ResolveResourceRequestType(
                addon, "meta", "series", "tt123"));
            Assert.Equal("series", AddonRepository.ResolveResourceRequestType(
                AddonWithResources(
                    new AddonResource { Name = "meta", Types = new[] { "series" } }),
                "meta", "series", "tt123"));
        }

        [Fact]
        public void ResolveResourceRequestType_RecoversUnambiguousIdType()
        {
            var addon = AddonWithResources(
                new AddonResource
                {
                    Name = "catalog",
                    Types = new[] { "anime" },
                    IdPrefixes = new[] { "kitsu" }
                });

            // Mismatched requested type recovered via unambiguous typed prefix owner.
            Assert.Equal("anime", AddonRepository.ResolveResourceRequestType(
                addon, "catalog", "movie", "kitsu123", allowIdTypeFallback: true));
            // Without fallback flag: empty.
            Assert.Equal("", AddonRepository.ResolveResourceRequestType(
                addon, "catalog", "movie", "kitsu123"));
            // Ambiguous recovery (two types on the owning resource): empty.
            var ambiguous = AddonWithResources(
                new AddonResource
                {
                    Name = "catalog",
                    Types = new[] { "anime", "manga" },
                    IdPrefixes = new[] { "kitsu" }
                });
            Assert.Equal("", AddonRepository.ResolveResourceRequestType(
                ambiguous, "catalog", "movie", "kitsu123", allowIdTypeFallback: true));
        }

        // ------------------------------------------------------------------
        // Manifest mapping details
        // ------------------------------------------------------------------

        [Fact]
        public void MapManifest_MapsCatalogExtraLegacyFormat()
        {
            var repo = CreateRepo(new MemoryKeyValueStore(), new FetchCounter());
            var manifest = new AddonManifest
            {
                Id = "org.legacy",
                Name = "Legacy",
                Catalogs = new[]
                {
                    new AddonManifestCatalog
                    {
                        Id = "top",
                        Type = "movie",
                        ExtraRequired = new[] { "genre" },
                        ExtraSupported = new[] { "search", "genre" }
                    }
                }
            };

            var addon = repo.MapManifest(manifest, "https://legacy.example");
            Assert.Contains(addon.Catalogs[0].Extra, entry => entry.Name == "search" && !entry.IsRequired);
            Assert.Contains(addon.Catalogs[0].Extra, entry => entry.Name == "genre" && entry.IsRequired);
            Assert.Equal(2, addon.Catalogs[0].Extra.Count);
        }

        [Fact]
        public void NormalizeManifestAssetUrl_ResolvesRelativeAndProtocolRelative()
        {
            Assert.Equal("https://cdn.example/logo.png",
                AddonRepository.NormalizeManifestAssetUrl("//cdn.example/logo.png", "https://x.example"));
            Assert.Equal("https://x.example/img/logo.png",
                AddonRepository.NormalizeManifestAssetUrl("/img/logo.png", "https://x.example/sub?q=1"));
            Assert.Equal("data:image/png;base64,AA",
                AddonRepository.NormalizeManifestAssetUrl("data:image/png;base64,AA", "https://x.example"));
            Assert.Null(AddonRepository.NormalizeManifestAssetUrl("  ", "https://x.example"));
        }

        // ------------------------------------------------------------------
        // Profile scoping
        // ------------------------------------------------------------------

        [Fact]
        public async Task ProfileScoping_IsolatesPerProfile()
        {
            var store = new MemoryKeyValueStore();
            var repo = CreateRepo(store, new FetchCounter());
            await repo.AddAddonAsync("https://p1only.example", profileId: "2");

            Assert.DoesNotContain("p1only", await repo.GetInstalledAddonUrlsAsync("1"));
            Assert.Contains("https://p1only.example", await repo.GetInstalledAddonUrlsAsync("2"));
        }
    }
}
