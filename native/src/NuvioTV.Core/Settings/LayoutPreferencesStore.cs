using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Settings
{
    /// <summary>
    /// Port of js/data/local/layoutPreferences.js (layoutPreferences envelope).
    /// Field names match the webapp JSON exactly for sync parity.
    /// </summary>
    public sealed class LayoutPreferencesData
    {
        [JsonPropertyName("hasChosenLayout")]
        public bool HasChosenLayout { get; set; }

        [JsonPropertyName("homeLayout")]
        public string HomeLayout { get; set; } = "modern";

        [JsonPropertyName("continueWatchingCardStyle")]
        public string ContinueWatchingCardStyle { get; set; } = "card";

        [JsonPropertyName("heroSectionEnabled")]
        public bool HeroSectionEnabled { get; set; } = true;

        [JsonPropertyName("discoverLocation")]
        public string DiscoverLocation { get; set; } = "in_search";

        [JsonPropertyName("heroCatalogKeys")]
        public List<string> HeroCatalogKeys { get; set; } = new List<string>();

        [JsonPropertyName("posterLabelsEnabled")]
        public bool PosterLabelsEnabled { get; set; } = true;

        [JsonPropertyName("catalogAddonNameEnabled")]
        public bool CatalogAddonNameEnabled { get; set; } = true;

        [JsonPropertyName("catalogTypeSuffixEnabled")]
        public bool CatalogTypeSuffixEnabled { get; set; } = true;

        [JsonPropertyName("modernLandscapePostersEnabled")]
        public bool ModernLandscapePostersEnabled { get; set; }

        [JsonPropertyName("modernHeroFullScreenBackdropEnabled")]
        public bool ModernHeroFullScreenBackdropEnabled { get; set; }

        [JsonPropertyName("classicFocusGradientEnabled")]
        public bool ClassicFocusGradientEnabled { get; set; }

        [JsonPropertyName("focusedPosterBackdropExpandEnabled")]
        public bool FocusedPosterBackdropExpandEnabled { get; set; } = true;

        [JsonPropertyName("focusedPosterBackdropExpandDelaySeconds")]
        public int FocusedPosterBackdropExpandDelaySeconds { get; set; } = 3;

        [JsonPropertyName("posterCardWidthDp")]
        public int PosterCardWidthDp { get; set; } = 126;

        [JsonPropertyName("posterCardCornerRadiusDp")]
        public int PosterCardCornerRadiusDp { get; set; } = 12;

        [JsonPropertyName("fastHorizontalNavigationEnabled")]
        public bool FastHorizontalNavigationEnabled { get; set; }

        [JsonPropertyName("detailPageTrailerButtonEnabled")]
        public bool DetailPageTrailerButtonEnabled { get; set; }

        [JsonPropertyName("preferExternalMetaAddonDetail")]
        public bool PreferExternalMetaAddonDetail { get; set; } = true;

        [JsonPropertyName("blurUnwatchedEpisodes")]
        public bool BlurUnwatchedEpisodes { get; set; }

        [JsonPropertyName("collapseSidebar")]
        public bool CollapseSidebar { get; set; }

        [JsonPropertyName("modernSidebar")]
        public bool ModernSidebar { get; set; }

        [JsonPropertyName("hideUnreleasedContent")]
        public bool HideUnreleasedContent { get; set; }

        [JsonPropertyName("showFullReleaseDate")]
        public bool ShowFullReleaseDate { get; set; } = true;

        [JsonPropertyName("useEpisodeThumbnailsInCw")]
        public bool UseEpisodeThumbnailsInCw { get; set; } = true;

        [JsonPropertyName("blurContinueWatchingNextUp")]
        public bool BlurContinueWatchingNextUp { get; set; }

        [JsonPropertyName("showUnairedNextUp")]
        public bool ShowUnairedNextUp { get; set; } = true;

        [JsonPropertyName("nextUpFromFurthestEpisode")]
        public bool NextUpFromFurthestEpisode { get; set; } = true;

        [JsonPropertyName("continueWatchingSortMode")]
        public string ContinueWatchingSortMode { get; set; } = "default";
    }

    /// <summary>Profile-scoped layoutPreferences store with webapp normalize rules.</summary>
    public static class LayoutPreferencesStore
    {
        public const string StorageKey = "layoutPreferences";

        private static readonly ProfileScopedStore<LayoutPreferencesData> Store =
            ProfileScopedStore<LayoutPreferencesData>.Create(StorageKey, Normalize, ShallowMerge);

        public static LayoutPreferencesData Normalize(LayoutPreferencesData value)
        {
            var merged = value ?? new LayoutPreferencesData();

            // hasChosenLayout: explicit value wins, otherwise any non-default payload counts.
            merged.HasChosenLayout = value != null && !HasOnlyDefaults(value) || merged.HasChosenLayout;

            merged.ContinueWatchingCardStyle =
                Is(merged.ContinueWatchingCardStyle, "card", "wide", "poster") ?? "card";
            merged.DiscoverLocation =
                Is(merged.DiscoverLocation, "in_search", "in_sidebar", "off") ?? "in_search";
            merged.ContinueWatchingSortMode =
                Is(merged.ContinueWatchingSortMode, "default", "split_upcoming", "streaming_style")
                    ?? "default";

            if (merged.PosterCardWidthDp < 72) merged.PosterCardWidthDp = 126;
            if (merged.PosterCardCornerRadiusDp < 0) merged.PosterCardCornerRadiusDp = 12;
            if (merged.FocusedPosterBackdropExpandDelaySeconds < 0)
            {
                merged.FocusedPosterBackdropExpandDelaySeconds = 3;
            }
            if (merged.ModernSidebar) merged.CollapseSidebar = false;
            return merged;
        }

        private static string Is(string value, params string[] allowed)
        {
            var normalized = (value ?? "").ToLowerInvariant();
            foreach (var a in allowed)
            {
                if (normalized == a) return normalized;
            }
            return null;
        }

        private static bool HasOnlyDefaults(LayoutPreferencesData value) =>
            value.HomeLayout == "modern" && !value.HasChosenLayout;

        private static LayoutPreferencesData ShallowMerge(
            LayoutPreferencesData current, LayoutPreferencesData partial)
        {
            // Settings screen writes whole objects (webapp spread semantics with
            // full payloads); fall back to whichever side is present.
            if (current == null) return partial ?? new LayoutPreferencesData();
            if (partial == null) return current;
            return partial;
        }

        public static System.Threading.Tasks.Task<LayoutPreferencesData> GetAsync(string profileId) =>
            Store.GetAsync(profileId);

        public static System.Threading.Tasks.Task SetAsync(string profileId, LayoutPreferencesData partial) =>
            Store.SetAsync(profileId, partial);
    }
}
