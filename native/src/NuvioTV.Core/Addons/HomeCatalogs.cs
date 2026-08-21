using System;
using System.Collections.Generic;
using System.Linq;
using NuvioTV.Core.Models;
namespace NuvioTV.Core.Addons
{
    /// <summary>Minimal collection input (js collection objects: id/title/folders).</summary>
    public sealed class HomeCollection
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public int FolderCount { get; set; }
    }

    /// <summary>One home-screen catalog row entry (js buildOrderedHomeCatalogItems entries).</summary>
    public sealed class HomeCatalogEntry
    {
        public string Key { get; set; }
        public string DisableKey { get; set; }
        public string AddonBaseUrl { get; set; }
        public string AddonId { get; set; }
        public string AddonName { get; set; }
        public string CatalogId { get; set; }
        public string CatalogName { get; set; }
        public string OriginalCatalogName { get; set; }
        public string Type { get; set; }
        public bool IsCollection { get; set; }
        public string CollectionId { get; set; }
        public bool IsDisabled { get; set; }
        public bool CanMoveUp { get; set; }
        public bool CanMoveDown { get; set; }
    }

    /// <summary>
    /// Ordered union of installed-addon catalogs for the home screen. Port of
    /// js/core/addons/homeCatalogs.js: catalogs without required extras only,
    /// dedupe by `${addonId}_${type}_${catalogId}`, saved-order/disabled-key/
    /// custom-title application, collections appended after addons.
    /// </summary>
    public static class HomeCatalogs
    {
        // homeCatalogs.js:1-5 catalogRequiresExtras.
        public static bool CatalogRequiresExtras(AddonCatalog catalog)
        {
            return (catalog?.Extra ?? new List<AddonCatalogExtra>())
                .Any(entry => entry != null && entry.IsRequired);
        }

        public static string BuildCatalogOrderKey(string addonId, string type, string catalogId)
        {
            return $"{addonId}_{type}_{catalogId}";
        }

        public static string BuildCatalogDisableKey(
            string addonBaseUrl, string type, string catalogId, string catalogName)
        {
            return $"{addonBaseUrl}_{type}_{catalogId}_{catalogName}";
        }

        public static string BuildCollectionOrderKey(string collectionId)
        {
            var id = (collectionId ?? "").Trim();
            return id.Length > 0 ? $"collection_{id}" : "";
        }

        // homeCatalogs.js:20-26 toDisplayTypeLabel.
        public static string ToDisplayTypeLabel(string value)
        {
            var raw = (value ?? "").Trim();
            if (raw.Length == 0)
            {
                return "";
            }
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
        }

        /// <summary>js buildOrderedCatalogItems — no collections.</summary>
        public static IReadOnlyList<HomeCatalogEntry> BuildOrderedCatalogItems(
            IEnumerable<Addon> addons,
            IEnumerable<string> savedOrderKeys = null,
            IEnumerable<string> disabledKeys = null,
            IReadOnlyDictionary<string, string> customTitles = null)
        {
            return BuildOrderedHomeCatalogItems(addons, null, savedOrderKeys, disabledKeys, customTitles);
        }

        /// <summary>
        /// js buildOrderedHomeCatalogItems(addons, collections, savedOrderKeys,
        /// disabledKeys, customTitles).
        /// </summary>
        public static IReadOnlyList<HomeCatalogEntry> BuildOrderedHomeCatalogItems(
            IEnumerable<Addon> addons,
            IEnumerable<HomeCollection> collections = null,
            IEnumerable<string> savedOrderKeys = null,
            IEnumerable<string> disabledKeys = null,
            IReadOnlyDictionary<string, string> customTitles = null)
        {
            var defaultEntries = new List<HomeCatalogEntry>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var disabledSet = new HashSet<string>(
                disabledKeys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            foreach (var addon in addons ?? Enumerable.Empty<Addon>())
            {
                if (addon == null)
                {
                    continue;
                }
                foreach (var catalog in addon.Catalogs ?? new List<AddonCatalog>())
                {
                    if (CatalogRequiresExtras(catalog))
                    {
                        continue;
                    }
                    var apiType = !string.IsNullOrEmpty(catalog?.ApiType) ? catalog.ApiType : catalog?.Type;
                    var key = BuildCatalogOrderKey(addon.Id, apiType, catalog.Id);
                    if (!seenKeys.Add(key))
                    {
                        continue;
                    }
                    var originalName = string.IsNullOrEmpty(catalog.Name) ? "" : catalog.Name;
                    defaultEntries.Add(new HomeCatalogEntry
                    {
                        Key = key,
                        DisableKey = BuildCatalogDisableKey(addon.BaseUrl, apiType, catalog.Id, originalName),
                        AddonBaseUrl = addon.BaseUrl,
                        AddonId = addon.Id,
                        AddonName = addon.DisplayName,
                        CatalogId = catalog.Id,
                        CatalogName = CustomTitleForKey(customTitles, key) ?? originalName,
                        OriginalCatalogName = originalName,
                        Type = apiType,
                        IsCollection = false,
                        IsDisabled = false
                    });
                }
            }

            foreach (var collection in collections ?? Enumerable.Empty<HomeCollection>())
            {
                var key = BuildCollectionOrderKey(collection?.Id);
                if (key.Length == 0 || !seenKeys.Add(key))
                {
                    continue;
                }
                var folderCount = collection.FolderCount;
                var title = collection.Title ?? "";
                defaultEntries.Add(new HomeCatalogEntry
                {
                    Key = key,
                    DisableKey = key,
                    AddonBaseUrl = "",
                    AddonId = "",
                    AddonName = folderCount == 1 ? "1 folder" : $"{folderCount} folders",
                    CatalogId = collection.Id,
                    CatalogName = CustomTitleForKey(customTitles, key) ?? title,
                    OriginalCatalogName = title,
                    Type = "collection",
                    IsCollection = true,
                    CollectionId = collection.Id,
                    IsDisabled = false
                });
            }

            var entryByKey = new Dictionary<string, HomeCatalogEntry>(StringComparer.Ordinal);
            foreach (var entry in defaultEntries)
            {
                entryByKey[entry.Key] = entry;
            }
            var defaultOrderKeys = defaultEntries.Select(entry => entry.Key).ToList();

            // Saved keys must be unique and reference a known entry (js dedupe filter).
            var savedValid = new List<string>();
            var savedSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in savedOrderKeys ?? Enumerable.Empty<string>())
            {
                if (key == null || !savedSeen.Add(key) || !entryByKey.ContainsKey(key))
                {
                    continue;
                }
                savedValid.Add(key);
            }
            var savedSet = new HashSet<string>(savedValid, StringComparer.Ordinal);
            var effectiveOrder = savedValid
                .Concat(defaultOrderKeys.Where(key => !savedSet.Contains(key)))
                .ToList();

            var result = new List<HomeCatalogEntry>();
            for (var index = 0; index < effectiveOrder.Count; index++)
            {
                if (!entryByKey.TryGetValue(effectiveOrder[index], out var source))
                {
                    continue;
                }
                var isDisabled = disabledSet.Contains(source.DisableKey) || disabledSet.Contains(source.Key);
                // js quirk: when only the order key was disabled, surface the order
                // key as the disable key so re-enabling targets the same entry.
                var disableKey = disabledSet.Contains(source.Key) && !disabledSet.Contains(source.DisableKey)
                    ? source.Key
                    : source.DisableKey;
                result.Add(new HomeCatalogEntry
                {
                    Key = source.Key,
                    DisableKey = disableKey,
                    AddonBaseUrl = source.AddonBaseUrl,
                    AddonId = source.AddonId,
                    AddonName = source.AddonName,
                    CatalogId = source.CatalogId,
                    CatalogName = source.CatalogName,
                    OriginalCatalogName = source.OriginalCatalogName,
                    Type = source.Type,
                    IsCollection = source.IsCollection,
                    CollectionId = source.CollectionId,
                    IsDisabled = isDisabled,
                    CanMoveUp = index > 0,
                    CanMoveDown = index < effectiveOrder.Count - 1
                });
            }
            return result;
        }

        /// <summary>
        /// Brief seam: ResolveHomeRows(installedAddons) — default-ordered union of
        /// catalogs without required extras, nothing disabled.
        /// </summary>
        public static IReadOnlyList<HomeCatalogEntry> ResolveHomeRows(IEnumerable<Addon> installedAddons)
        {
            return BuildOrderedHomeCatalogItems(installedAddons);
        }

        private static string CustomTitleForKey(
            IReadOnlyDictionary<string, string> customTitles, string key)
        {
            if (customTitles != null &&
                customTitles.TryGetValue(key ?? "", out var title) &&
                !string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }
            return null;
        }
    }
}
