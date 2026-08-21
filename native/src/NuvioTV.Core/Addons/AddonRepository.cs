using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Models;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Addons
{
    /// <summary>Fetch outcome (js safeApiCall result shape).</summary>
    public sealed class AddonFetchResult
    {
        public string Status { get; }
        public Models.Addon Data { get; }
        public string Message { get; }

        private AddonFetchResult(string status, Models.Addon data, string message)
        {
            Status = status;
            Data = data;
            Message = message;
        }

        public static AddonFetchResult Success(Models.Addon data)
        {
            return new AddonFetchResult("success", data, null);
        }

        public static AddonFetchResult Error(string message)
        {
            return new AddonFetchResult("error", null, message);
        }
    }

    /// <summary>
    /// Installed-addon repository. Verbatim port of js/data/repository/addonRepository.js:
    /// profile-scoped persistence (installedAddonUrls / installedAddonDisplayNames /
    /// installedAddonEnabledStates), manifest cache + in-flight dedupe + builtin
    /// cinemeta fallback, display-name overrides with duplicate suffixing,
    /// resource/type/idPrefix matching with id-type recovery.
    /// </summary>
    public sealed class AddonRepository
    {
        private const string AddonUrlsKey = "installedAddonUrls";
        private const string AddonDisplayNamesKey = "installedAddonDisplayNames";
        private const string AddonEnabledStatesKey = "installedAddonEnabledStates";

        private static readonly string[] DefaultAddonUrls =
        {
            StremioAddonClient.DefaultCinemetaUrl,
            StremioAddonClient.DefaultOpensubtitlesUrl
        };

        private readonly IKeyValueStore _store;
        private readonly Func<string, CancellationToken, Task<AddonManifest>> _manifestFetcher;

        private readonly object _lock = new object();
        private readonly Dictionary<string, Models.Addon> _manifestCache =
            new Dictionary<string, Models.Addon>();
        private readonly Dictionary<string, AddonFetchResult> _manifestErrorCache =
            new Dictionary<string, AddonFetchResult>();
        private readonly Dictionary<string, Task<AddonFetchResult>> _manifestRequests =
            new Dictionary<string, Task<AddonFetchResult>>();
        private IReadOnlyList<Models.Addon> _installedAddonsCache;
        private string _installedAddonsCacheKey = "";
        private Task<IReadOnlyList<Models.Addon>> _installedAddonsPromise;
        private string _installedAddonsPromiseKey = "";

        /// <summary>
        /// Active-profile seam (js ProfileManager.getActiveProfileId). Returns the
        /// default profile when unset; later boot tasks wire this to ProfileManager.
        /// </summary>
        public Func<string> ActiveProfileIdProvider { get; set; }

        /// <summary>Fires with a change reason ("add"/"remove"/"refresh"/"reorder").</summary>
        public event Action<string> OnInstalledAddonsChanged;

        public AddonRepository(
            IKeyValueStore store,
            StremioAddonClient client = null,
            Func<string, CancellationToken, Task<AddonManifest>> manifestFetcher = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (manifestFetcher == null)
            {
                if (client == null)
                {
                    throw new ArgumentException(
                        "Either client or manifestFetcher must be provided", nameof(client));
                }
                manifestFetcher = (baseUrl, token) => client.FetchManifestAsync(baseUrl, token);
            }
            _manifestFetcher = manifestFetcher;
        }


        // ------------------------------------------------------------------
        // Persistence seams
        // ------------------------------------------------------------------

        private ProfileScopedStore<IReadOnlyList<string>> UrlsStore()
        {
            return ProfileScopedStore<IReadOnlyList<string>>.CreateWithStore(
                AddonUrlsKey,
                _store,
                normalize: value => NormalizeUrlList(value),
                merge: null);
        }

        private ProfileScopedStore<Dictionary<string, string>> DisplayNamesStore()
        {
            return ProfileScopedStore<Dictionary<string, string>>.CreateWithStore(
                AddonDisplayNamesKey,
                _store,
                normalize: value => NormalizeDisplayNameOverrides(value),
                merge: null);
        }

        private ProfileScopedStore<Dictionary<string, bool>> EnabledStatesStore()
        {
            return ProfileScopedStore<Dictionary<string, bool>>.CreateWithStore(
                AddonEnabledStatesKey,
                _store,
                normalize: value => NormalizeAddonEnabledStates(value),
                merge: null);
        }

        private string ActiveProfileId(string profileId)
        {
            var raw = (profileId ?? (ActiveProfileIdProvider != null ? ActiveProfileIdProvider() : null) ?? "1")
                .Trim();
            return raw.Length > 0 ? raw : "1";
        }

        // ------------------------------------------------------------------
        // Normalizers (js 171-203)
        // ------------------------------------------------------------------

        private static IReadOnlyList<string> NormalizeUrlList(IReadOnlyList<string> value)
        {
            if (value == null)
            {
                return DefaultAddonUrls.ToList().AsReadOnly();
            }
            var seen = new List<string>();
            foreach (var url in value)
            {
                var clean = AddonUrlBuilder.CanonicalizeUrl(url);
                if (!string.IsNullOrEmpty(clean) && !seen.Contains(clean))
                {
                    seen.Add(clean);
                }
            }
            return seen.AsReadOnly();
        }

        private static Dictionary<string, string> NormalizeDisplayNameOverrides(
            Dictionary<string, string> value)
        {
            var result = new Dictionary<string, string>();
            if (value == null)
            {
                return result;
            }
            foreach (var entry in value)
            {
                var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(entry.Key);
                var cleanName = (entry.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(cleanUrl) && !string.IsNullOrEmpty(cleanName))
                {
                    result[cleanUrl] = cleanName;
                }
            }
            return result;
        }

        private static Dictionary<string, bool> NormalizeAddonEnabledStates(
            Dictionary<string, bool> value)
        {
            var result = new Dictionary<string, bool>();
            if (value == null)
            {
                return result;
            }
            foreach (var entry in value)
            {
                var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                    AddonUrlBuilder.CanonicalizeUrl(entry.Key));
                if (!string.IsNullOrEmpty(cleanUrl))
                {
                    result[cleanUrl] = entry.Value;
                }
            }
            return result;
        }

        public async Task<IReadOnlyList<string>> GetInstalledAddonUrlsAsync(
            string profileId = null)
        {
            return await UrlsStore().GetAsync(ActiveProfileId(profileId)).ConfigureAwait(false);
        }

        public async Task<bool> IsAddonEnabledAsync(string url, string profileId = null)
        {
            var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                AddonUrlBuilder.CanonicalizeUrl(url));
            if (string.IsNullOrEmpty(cleanUrl))
            {
                return false;
            }
            var states = await EnabledStatesStore()
                .GetAsync(ActiveProfileId(profileId)).ConfigureAwait(false);
            return !states.TryGetValue(cleanUrl, out var enabled) || enabled;
        }

        /// <summary>js setAddonEnabledStates — replace=false merges into current.</summary>
        private async Task<bool> SetAddonEnabledStatesAsync(
            IEnumerable<KeyValuePair<string, bool>> entries, bool replace,
            string profileId)
        {
            var store = EnabledStatesStore();
            var pid = ActiveProfileId(profileId);
            var next = replace
                ? new Dictionary<string, bool>()
                : new Dictionary<string, bool>(await store.GetAsync(pid).ConfigureAwait(false));
            foreach (var entry in entries)
            {
                var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                    AddonUrlBuilder.CanonicalizeUrl(entry.Key));
                if (!string.IsNullOrEmpty(cleanUrl))
                {
                    next[cleanUrl] = entry.Value;
                }
            }
            var changed = Serialize(await store.GetAsync(pid).ConfigureAwait(false)) != Serialize(next);
            if (changed)
            {
                await store.ReplaceAsync(pid, next).ConfigureAwait(false);
                InvalidateInstalledAddonsCache();
            }
            return changed;
        }

        public Task<bool> SetEnabledAsync(string url, bool enabled, string profileId = null)
        {
            return SetAddonEnabledStatesAsync(
                new[] { new KeyValuePair<string, bool>(url, enabled) }, replace: false,
                profileId: profileId);
        }

        public async Task<string> GetAddonDisplayNameOverrideAsync(
            string url, string profileId = null)
        {
            var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(url);
            if (string.IsNullOrEmpty(cleanUrl))
            {
                return "";
            }
            var overrides = await DisplayNamesStore()
                .GetAsync(ActiveProfileId(profileId)).ConfigureAwait(false);
            return overrides.TryGetValue(cleanUrl, out var name) ? name : "";
        }

        public async Task<bool> SetDisplayNameAsync(
            string url, string displayName, string profileId = null)
        {
            var store = DisplayNamesStore();
            var pid = ActiveProfileId(profileId);
            var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(url);
            if (string.IsNullOrEmpty(cleanUrl))
            {
                return false;
            }
            var next = new Dictionary<string, string>(
                await store.GetAsync(pid).ConfigureAwait(false));
            var name = (displayName ?? "").Trim();
            if (name.Length > 0)
            {
                next[cleanUrl] = name;
            }
            else
            {
                next.Remove(cleanUrl);
            }
            var changed = Serialize(await store.GetAsync(pid).ConfigureAwait(false)) != Serialize(next);
            if (changed)
            {
                await store.ReplaceAsync(pid, next).ConfigureAwait(false);
                InvalidateInstalledAddonsCache();
            }
            return changed;
        }

        // ------------------------------------------------------------------
        // Manifest fetching (js fetchAddon 296-350)
        // ------------------------------------------------------------------

        public async Task<AddonFetchResult> FetchAddonAsync(
            string baseUrl, bool force = false, bool preferCache = false,
            string profileId = null, CancellationToken ct = default)
        {
            var cleanBaseUrl = AddonUrlBuilder.CanonicalizeUrl(baseUrl);
            var manifestUrl = AddonUrlBuilder.BuildManifestUrl(cleanBaseUrl);

            if (!force && preferCache)
            {
                Models.Addon cached = null;
                AddonFetchResult cachedError = null;
                lock (_lock)
                {
                    _manifestCache.TryGetValue(cleanBaseUrl, out cached);
                    if (cached == null)
                    {
                        _manifestErrorCache.TryGetValue(cleanBaseUrl, out cachedError);
                    }
                }
                if (cached != null)
                {
                    return AddonFetchResult.Success(
                        await WithDisplayNameOverride(cached, profileId).ConfigureAwait(false));
                }
                if (cachedError != null)
                {
                    return cachedError;
                }
            }

            Task<AddonFetchResult> request;
            lock (_lock)
            {
                if (!force && _manifestRequests.TryGetValue(cleanBaseUrl, out var pending))
                {
                    request = pending;
                }
                else
                {
                    request = RunManifestRequestAsync(cleanBaseUrl, manifestUrl, profileId, ct);
                    _manifestRequests[cleanBaseUrl] = request;
                }
            }

            try
            {
                return await request.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    if (_manifestRequests.TryGetValue(cleanBaseUrl, out var current) &&
                        current == request)
                    {
                        _manifestRequests.Remove(cleanBaseUrl);
                    }
                }
            }
        }

        private async Task<AddonFetchResult> RunManifestRequestAsync(
            string cleanBaseUrl, string manifestUrl, string profileId, CancellationToken ct)
        {
            try
            {
                var manifest = await _manifestFetcher(manifestUrl, ct).ConfigureAwait(false);
                var addon = MapManifest(manifest ?? new AddonManifest(), cleanBaseUrl);
                lock (_lock)
                {
                    _manifestCache[cleanBaseUrl] = addon;
                    _manifestErrorCache.Remove(cleanBaseUrl);
                }
                return AddonFetchResult.Success(
                    await WithDisplayNameOverride(addon, profileId).ConfigureAwait(false));
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Models.Addon cached = null;
                lock (_lock)
                {
                    _manifestCache.TryGetValue(cleanBaseUrl, out cached);
                }
                if (cached != null)
                {
                    return AddonFetchResult.Success(
                        await WithDisplayNameOverride(cached, profileId).ConfigureAwait(false));
                }


                var fallbackManifest = AddonCanonicalizer.GetBuiltinFallbackManifest(cleanBaseUrl);
                if (fallbackManifest != null)
                {
                    var fallback = MapManifest(fallbackManifest, cleanBaseUrl);
                    lock (_lock)
                    {
                        _manifestCache[cleanBaseUrl] = fallback;
                        _manifestErrorCache.Remove(cleanBaseUrl);
                    }
                    return AddonFetchResult.Success(
                        await WithDisplayNameOverride(fallback, profileId).ConfigureAwait(false));
                }

                var result = AddonFetchResult.Error(error.Message ?? "addon manifest failed");
                lock (_lock)
                {
                    _manifestErrorCache[cleanBaseUrl] = result;
                }
                return result;
            }
        }

        public void InvalidateInstalledAddonsCache()
        {
            lock (_lock)
            {
                _installedAddonsCache = null;
                _installedAddonsCacheKey = "";
                _installedAddonsPromise = null;
                _installedAddonsPromiseKey = "";
            }
        }

        // ------------------------------------------------------------------
        // Installed addons (js getCachedInstalledAddons/getInstalledAddons)
        // ------------------------------------------------------------------

        public async Task<IReadOnlyList<Models.Addon>> GetCachedInstalledAddonsAsync(
            IReadOnlyList<string> urls = null, bool includeDisabled = false,
            string profileId = null)
        {
            var normalizedUrls = urls ?? await GetInstalledAddonUrlsAsync(profileId)
                .ConfigureAwait(false);
            var selectedUrls = includeDisabled
                ? normalizedUrls
                : normalizedUrls.Where(url => IsAddonEnabledStateSync(url, urls == null)).ToList();

            List<Models.Addon> addons;
            lock (_lock)
            {
                addons = selectedUrls
                    .Select(url => _manifestCache.TryGetValue(
                        AddonUrlBuilder.CanonicalizeUrl(url), out var addon) ? addon : null)
                    .Where(addon => addon != null)
                    .ToList();
            }
            return ApplyDisplayNames(addons, profileId);
        }

        private bool IsAddonEnabledStateSync(string url, bool useDefaultProfileOnly)
        {
            // Sync path only valid for the active/default profile; callers needing
            // per-profile checks go through GetInstalledAddonsAsync.
            var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                AddonUrlBuilder.CanonicalizeUrl(url));
            if (string.IsNullOrEmpty(cleanUrl))
            {
                return false;
            }
            var task = EnabledStatesStore().GetAsync(ActiveProfileId(null));
            var states = task.GetAwaiter().GetResult();
            return !states.TryGetValue(cleanUrl, out var enabled) || enabled;
        }

        public async Task<IReadOnlyList<Models.Addon>> GetInstalledAddonsAsync(
            bool includeDisabled = false, bool force = false, bool cacheOnly = false,
            string profileId = null, CancellationToken ct = default)
        {
            var pid = ActiveProfileId(profileId);
            var allUrls = await GetInstalledAddonUrlsAsync(pid).ConfigureAwait(false);
            var states = await EnabledStatesStore().GetAsync(pid).ConfigureAwait(false);
            var urls = includeDisabled
                ? allUrls
                : allUrls.Where(url =>
                    !states.TryGetValue(NormalizeForStateKey(url), out var enabled) || enabled)
                    .ToList();
            var overrides = await DisplayNamesStore().GetAsync(pid).ConfigureAwait(false);

            var cacheKey = Serialize(new Dictionary<string, object>
            {
                ["profileId"] = pid,
                ["urls"] = urls,
                ["displayNames"] = overrides,
                ["enabledStates"] = states,
                ["includeDisabled"] = includeDisabled
            });

            if (!force)
            {
                lock (_lock)
                {
                    if (_installedAddonsCache != null && _installedAddonsCacheKey == cacheKey)
                    {
                        return _installedAddonsCache.ToList().AsReadOnly();
                    }
                }
            }

            if (cacheOnly)
            {
                return await GetCachedInstalledAddonsAsync(urls, includeDisabled, pid)
                    .ConfigureAwait(false);
            }

            Task<IReadOnlyList<Models.Addon>> request = null;
            Task<IReadOnlyList<Models.Addon>> existing = null;
            lock (_lock)
            {
                if (!force && _installedAddonsPromise != null &&
                    _installedAddonsPromiseKey == cacheKey)
                {
                    existing = _installedAddonsPromise;
                }
                else
                {
                    request = RunInstalledAddonsRequestAsync(
                        urls, force, pid, cacheKey, includeDisabled, ct);
                    _installedAddonsPromise = request;
                    _installedAddonsPromiseKey = cacheKey;
                }
            }

            if (existing != null)
            {
                return await existing.ConfigureAwait(false);
            }

            try
            {
                return await request.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    if (_installedAddonsPromise == request)
                    {
                        _installedAddonsPromise = null;
                        _installedAddonsPromiseKey = "";
                    }
                }
            }
        }

        private async Task<IReadOnlyList<Models.Addon>> RunInstalledAddonsRequestAsync(
            IReadOnlyList<string> urls, bool force, string pid, string cacheKey,
            bool includeDisabled, CancellationToken ct)
        {
            var fetched = new List<AddonFetchResult>();
            foreach (var url in urls)
            {
                fetched.Add(await FetchAddonAsync(url, force: force, preferCache: !force,
                    profileId: pid, ct: ct).ConfigureAwait(false));
            }

            var addons = fetched
                .Where(result => result.Status == "success")
                .Select(result => result.Data)
                .ToList();

            var displayAddons = ApplyDisplayNames(addons, pid);
            lock (_lock)
            {
                if (_installedAddonsCacheKey == "" || _installedAddonsCacheKey != cacheKey)
                {
                    _installedAddonsCache = displayAddons;
                    _installedAddonsCacheKey = cacheKey;
                }
            }
            return displayAddons.ToList().AsReadOnly();
        }

        // ------------------------------------------------------------------
        // Mutations (js addAddon/removeAddon/refreshAddon/setAddonOrder)
        // ------------------------------------------------------------------

        public async Task<bool?> AddAddonAsync(string url, string profileId = null)
        {
            var clean = AddonCanonicalizer.NormalizeCinemetaUrl(
                AddonUrlBuilder.CanonicalizeUrl(url));
            if (string.IsNullOrEmpty(clean))
            {
                return null;
            }

            var current = await GetInstalledAddonUrlsAsync(profileId).ConfigureAwait(false);
            if (current.Contains(clean))
            {
                return false;
            }

            await UrlsStore().ReplaceAsync(ActiveProfileId(profileId),
                current.Concat(new[] { clean }).ToList()).ConfigureAwait(false);
            await SetAddonEnabledStatesAsync(
                new[] { new KeyValuePair<string, bool>(clean, true) }, replace: false,
                profileId: profileId).ConfigureAwait(false);
            lock (_lock)
            {
                _manifestErrorCache.Remove(clean);
            }
            InvalidateInstalledAddonsCache();
            NotifyChanged("add");
            return true;
        }

        public async Task<bool> RemoveAddonAsync(string url, string profileId = null)
        {
            var clean = AddonCanonicalizer.NormalizeCinemetaUrl(
                AddonUrlBuilder.CanonicalizeUrl(url));
            var current = await GetInstalledAddonUrlsAsync(profileId).ConfigureAwait(false);
            var next = current.Where(value => AddonUrlBuilder.CanonicalizeUrl(value) != clean)
                .ToList();
            if (next.Count == current.Count)
            {
                return false;
            }
            await UrlsStore().ReplaceAsync(ActiveProfileId(profileId), next)
                .ConfigureAwait(false);

            var statesStore = EnabledStatesStore();
            var states = new Dictionary<string, bool>(
                await statesStore.GetAsync(ActiveProfileId(profileId)).ConfigureAwait(false));
            states.Remove(clean);
            await statesStore.ReplaceAsync(ActiveProfileId(profileId), states)
                .ConfigureAwait(false);

            lock (_lock)
            {
                _manifestCache.Remove(clean);
                _manifestErrorCache.Remove(clean);
            }
            InvalidateInstalledAddonsCache();
            NotifyChanged("remove");
            return true;
        }

        public async Task<AddonFetchResult> RefreshAddonAsync(
            string url, string profileId = null, CancellationToken ct = default)
        {
            var clean = AddonCanonicalizer.NormalizeCinemetaUrl(
                AddonUrlBuilder.CanonicalizeUrl(url));
            if (string.IsNullOrEmpty(clean))
            {
                return AddonFetchResult.Error("Invalid addon URL");
            }

            lock (_lock)
            {
                _manifestCache.Remove(clean);
                _manifestErrorCache.Remove(clean);
            }
            InvalidateInstalledAddonsCache();
            var result = await FetchAddonAsync(clean, force: true, profileId: profileId, ct: ct)
                .ConfigureAwait(false);
            if (result.Status == "success")
            {
                NotifyChanged("refresh");
            }
            return result;
        }

        public async Task<bool> SetAddonOrderAsync(
            IReadOnlyList<string> urls, bool silent = false, string profileId = null)
        {
            var normalized = (urls ?? Array.Empty<string>())
                .Select(url => AddonCanonicalizer.NormalizeCinemetaUrl(
                    AddonUrlBuilder.CanonicalizeUrl(url)))
                .Where(url => !string.IsNullOrEmpty(url))
                .ToList();
            var pid = ActiveProfileId(profileId);

            var current = await GetInstalledAddonUrlsAsync(pid).ConfigureAwait(false);
            var statesStore = EnabledStatesStore();
            var currentStates = await statesStore.GetAsync(pid).ConfigureAwait(false);
            var nextStates = new Dictionary<string, bool>();
            foreach (var url in normalized)
            {
                nextStates[url] = !currentStates.TryGetValue(url, out var enabled) || enabled;
            }

            var changed = Serialize(current) != Serialize(normalized);
            var enabledStatesChanged = Serialize(currentStates) != Serialize(nextStates);

            await UrlsStore().ReplaceAsync(pid, normalized).ConfigureAwait(false);
            if (enabledStatesChanged)
            {
                await statesStore.ReplaceAsync(pid, nextStates).ConfigureAwait(false);
            }
            if (changed || enabledStatesChanged)
            {
                var normalizedSet = new HashSet<string>(normalized);
                lock (_lock)
                {
                    foreach (var url in current.Where(url => !normalizedSet.Contains(url)))
                    {
                        _manifestCache.Remove(url);
                        _manifestErrorCache.Remove(url);
                    }
                }
                InvalidateInstalledAddonsCache();
            }
            if ((changed || enabledStatesChanged) && !silent)
            {
                NotifyChanged("reorder");
            }
            return changed || enabledStatesChanged;
        }

        private void NotifyChanged(string reason)
        {
            InvalidateInstalledAddonsCache();
            OnInstalledAddonsChanged?.Invoke(reason);
        }

        // ------------------------------------------------------------------
        // Display names (js withDisplayNameOverride/applyDisplayNames)
        // ------------------------------------------------------------------

        private async Task<Models.Addon> WithDisplayNameOverride(
            Models.Addon addon, string profileId)
        {
            var override_ = await GetAddonDisplayNameOverrideAsync(
                addon?.BaseUrl, profileId).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(override_) && override_ != addon.Name)
            {
                return CopyWithDisplayName(addon, override_);
            }
            return addon;
        }

        private IReadOnlyList<Models.Addon> ApplyDisplayNames(
            IReadOnlyList<Models.Addon> addons, string profileId)
        {
            var decorated = new List<Models.Addon>();
            foreach (var addon in addons ?? (IReadOnlyList<Models.Addon>)Array.Empty<Models.Addon>())
            {
                var overrideTask = WithDisplayNameOverride(addon, profileId);
                decorated.Add(overrideTask.GetAwaiter().GetResult());
            }

            var nameCount = new Dictionary<string, int>();
            foreach (var addon in decorated.Where(addon => addon.DisplayName == addon.Name))
            {
                nameCount[addon.Name] = (nameCount.TryGetValue(addon.Name, out var count)
                    ? count : 0) + 1;
            }

            var counters = new Dictionary<string, int>();
            var output = new List<Models.Addon>();
            foreach (var addon in decorated)
            {
                if (addon.DisplayName != addon.Name ||
                    !nameCount.TryGetValue(addon.Name, out var total) || total <= 1)
                {
                    output.Add(addon);
                    continue;
                }
                counters[addon.Name] = (counters.TryGetValue(addon.Name, out var occurrence)
                    ? occurrence : 0) + 1;
                output.Add(counters[addon.Name] == 1
                    ? addon
                    : CopyWithDisplayName(addon, addon.Name + " (" + counters[addon.Name] + ")"));
            }
            return output;
        }

        private static Models.Addon CopyWithDisplayName(Models.Addon addon, string displayName)
        {
            return new Models.Addon(
                addon.Id, addon.Name, displayName, addon.Version, addon.Description,
                addon.Logo, addon.BaseUrl, addon.Catalogs, addon.Types, addon.RawTypes,
                addon.IdPrefixes, addon.Resources);
        }

        // ------------------------------------------------------------------
        // Manifest mapping (js mapManifest 598-664)
        // ------------------------------------------------------------------

        public Models.Addon MapManifest(AddonManifest manifest, string baseUrl)
        {
            manifest = manifest ?? new AddonManifest();
            var types = (manifest.Types ?? Array.Empty<string>())
                .Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0)
                .ToList();

            var catalogs = (manifest.Catalogs ?? Array.Empty<AddonManifestCatalog>())
                .Select(catalog => new AddonCatalog
                {
                    Id = catalog.Id,
                    Name = FirstNonEmpty(catalog.Name, catalog.Id),
                    ApiType = (catalog.Type ?? "").Trim(),
                    Extra = MapCatalogExtra(catalog)
                })
                .ToList();

            return new Models.Addon(
                FirstNonEmpty(manifest.Id, baseUrl),
                FirstNonEmpty(manifest.Name, "Unknown Addon"),
                FirstNonEmpty(manifest.Name, "Unknown Addon"),
                FirstNonEmpty(manifest.Version, "0.0.0"),
                string.IsNullOrEmpty(manifest.Description) ? null : manifest.Description,
                NormalizeManifestAssetUrl(manifest.Logo, baseUrl),
                baseUrl,
                catalogs,
                types,
                types.ToList(),
                manifest.IdPrefixes ?? Array.Empty<string>(),
                ParseResources(manifest.Resources, types));
        }

        private static IReadOnlyList<AddonCatalogExtra> MapCatalogExtra(AddonManifestCatalog catalog)
        {
            catalog = catalog ?? new AddonManifestCatalog();
            if (catalog.Extra != null)
            {
                return catalog.Extra.Select(entry => new AddonCatalogExtra
                {
                    Name = entry.Name,
                    IsRequired = entry.IsRequired,
                    Options = null
                }).ToList();
            }

            // Legacy manifest format: extraSupported/extraRequired as plain name arrays.
            var required = catalog.ExtraRequired ?? Array.Empty<string>();
            var supported = catalog.ExtraSupported ?? Array.Empty<string>();
            var names = supported.Concat(required.Where(name => !supported.Contains(name)));
            return names.Select(name => new AddonCatalogExtra
            {
                Name = Convert.ToString(name, System.Globalization.CultureInfo.InvariantCulture),
                IsRequired = required.Contains(name),
                Options = null
            }).ToList();
        }

        private IReadOnlyList<AddonResource> ParseResources(
            IReadOnlyList<AddonManifestResource> resources, IReadOnlyList<string> defaultTypes)
        {
            var parsed = new List<AddonResource>();
            foreach (var resource in resources ?? Array.Empty<AddonManifestResource>())
            {
                parsed.Add(new AddonResource
                {
                    Name = resource.Name ?? "",
                    Types = resource.Types ?? defaultTypes.ToList(),
                    IdPrefixes = resource.IdPrefixes != null
                        ? resource.IdPrefixes.ToList()
                        : new List<string>()
                });
            }
            return parsed;
        }

        /// <summary>js normalizeManifestAssetUrl (48-68).</summary>
        public static string NormalizeManifestAssetUrl(string value, string baseUrl)
        {
            var raw = (value ?? "").Trim();
            if (raw.Length == 0)
            {
                return null;
            }
            if (raw.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + raw;
            }
            var lower = raw.ToLowerInvariant();
            if (lower.StartsWith("https:", StringComparison.Ordinal) ||
                lower.StartsWith("http:", StringComparison.Ordinal) ||
                lower.StartsWith("data:", StringComparison.Ordinal) ||
                lower.StartsWith("blob:", StringComparison.Ordinal))
            {
                return raw;
            }

            var cleanBaseUrl = AddonUrlBuilder.CanonicalizeUrl(baseUrl ?? "");
            var queryStart = cleanBaseUrl.IndexOf('?');
            var basePath = queryStart >= 0
                ? cleanBaseUrl.Substring(0, queryStart).TrimEnd('/')
                : cleanBaseUrl.TrimEnd('/');
            if (Uri.TryCreate(
                    new Uri((basePath.Length > 0 ? basePath : "https://localhost") + "/"),
                    raw, out var resolved))
            {
                return resolved.AbsoluteUri;
            }
            return raw;
        }

        // ------------------------------------------------------------------
        // Resource matching (js getResourceTypes/resourceSupportsType/
        // resourceSupportsId/resolveResourceRequestType)
        // ------------------------------------------------------------------

        public static IReadOnlyList<string> GetResourceTypes(AddonResource resource)
        {
            return (resource?.Types ?? Array.Empty<string>())
                .Where(type => !string.IsNullOrEmpty((type ?? "").Trim()))
                .Select(type => type.Trim())
                .ToList();
        }

        public static IReadOnlyList<string> GetResourceIdPrefixes(
            Models.Addon addon, AddonResource resource)
        {
            var prefixes = resource?.IdPrefixes != null && resource.IdPrefixes.Count > 0
                ? resource.IdPrefixes
                : addon?.IdPrefixes ?? Array.Empty<string>();
            return prefixes
                .Select(prefix => (prefix ?? "").Trim())
                .Where(prefix => prefix.Length > 0)
                .ToList();
        }

        public static bool ResourceSupportsType(AddonResource resource, string type)
        {
            var targetType = (type ?? "").Trim().ToLowerInvariant();
            if (targetType.Length == 0)
            {
                return false;
            }
            var types = GetResourceTypes(resource)
                .Select(resourceType => resourceType.ToLowerInvariant()).ToList();
            return types.Count == 0 || types.Contains(targetType);
        }

        public static bool ResourceSupportsId(
            Models.Addon addon, AddonResource resource, string id,
            bool caseInsensitive = false)
        {
            var prefixes = GetResourceIdPrefixes(addon, resource);
            if (prefixes.Count == 0)
            {
                return true;
            }
            var rawId = id ?? "";
            if (caseInsensitive)
            {
                var normalizedId = rawId.ToLowerInvariant();
                return prefixes.Any(prefix => normalizedId.StartsWith(prefix.ToLowerInvariant()));
            }
            return prefixes.Any(prefix => rawId.StartsWith(prefix));
        }

        /// <summary>
        /// js resolveResourceRequestType: match by resource name + ID prefix, keep the
        /// requested type when supported, otherwise recover it from unambiguous typed
        /// prefix-owning resources (allowIdTypeFallback).
        /// </summary>
        public static string ResolveResourceRequestType(
            Models.Addon addon, string resourceName, string requestedType, string id,
            bool caseInsensitive = false, bool allowIdTypeFallback = false)
        {
            var targetResource = (resourceName ?? "").Trim().ToLowerInvariant();
            var cleanRequestedType = (requestedType ?? "").Trim();
            var resources = ((addon?.Resources ?? Array.Empty<AddonResource>()) as IEnumerable<AddonResource>)
                ?.Where(resource =>
                    string.Equals((resource?.Name ?? "").Trim().ToLowerInvariant(),
                        targetResource, StringComparison.Ordinal) &&
                    ResourceSupportsId(addon, resource, id, caseInsensitive))
                .ToList() ?? new List<AddonResource>();
            if (resources.Count == 0)
            {
                return "";
            }
            if (cleanRequestedType.Length > 0 &&
                resources.Any(resource => ResourceSupportsType(resource, cleanRequestedType)))
            {
                return cleanRequestedType;
            }
            if (!allowIdTypeFallback)
            {
                return "";
            }

            // A matching ID prefix is strong ownership evidence. Recover a mismatched
            // catalog type only when the owning resource declares one unambiguous type.
            var recoveredTypes = new List<string>();
            foreach (var resource in resources)
            {
                if (GetResourceIdPrefixes(addon, resource).Count == 0)
                {
                    continue;
                }
                var candidateTypes = GetResourceTypes(resource).Count > 0
                    ? GetResourceTypes(resource)
                    : addon.RawTypes ?? addon.Types ?? (IReadOnlyList<string>)new List<string>();
                foreach (var type in candidateTypes)
                {
                    var cleanType = (type ?? "").Trim();
                    if (cleanType.Length > 0 &&
                        !recoveredTypes.Any(existing =>
                            existing.ToLowerInvariant() == cleanType.ToLowerInvariant()))
                    {
                        recoveredTypes.Add(cleanType);
                    }
                }
            }
            return recoveredTypes.Count == 1 ? recoveredTypes[0] : "";
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string NormalizeForStateKey(string url)
        {
            return AddonCanonicalizer.NormalizeCinemetaUrl(AddonUrlBuilder.CanonicalizeUrl(url));
        }

        private static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, LocalStore.JsonOptions);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }
    }
}
