using System;
using System.Collections.Generic;
using System.Linq;
using NuvioTV.Core.Models;

namespace NuvioTV.Core.Home
{
    /// <summary>
    /// Home screen budgets (homeConstants.js) and the pure scheduling /
    /// windowing logic of the home data pipeline: Continue-Watching next-up
    /// candidate windowing, render batching, and hero rotation scheduling.
    /// </summary>
    public static class HomeEngine
    {
        // ---- homeConstants.js ----
        public const int HeroRotateFirstDelayMs = 20000;
        public const int HeroRotateIntervalMs = 10000;
        public static readonly string[] HomeLayoutSequence = { "modern", "grid", "classic" };

        public const int CwMaxNextUpLookups = 32;
        public const int CwMaxNextUpConcurrency = 4;
        public const int CwMaxVisibleItems = 300;
        public const int CwDisplaySnapshotMaxItems = 50;
        public const int CwInitialResolveBudgetMs = 1000;
        public const int CwRenderBatchItems = 30;
        public const int CwRenderLoadAheadItems = 4;
        public const int CwDaysCap = 60;

        public const int HomeInitialCatalogLoad = 10;
        public const int HomeMaxItemsPerRow = 15;
        public const int HomeRowTimeoutMs = 3500;

        /// <summary>Cycles the L-key layout order.</summary>
        public static string NextLayout(string current)
        {
            var index = Array.IndexOf(HomeLayoutSequence, (current ?? "modern").ToLowerInvariant());
            return HomeLayoutSequence[(index + 1) % HomeLayoutSequence.Length];
        }

        /// <summary>
        /// CW windowing (continueWatchingRenderWindow port): from progress
        /// entries, keep started-not-finished items within the 60-day cap,
        /// drop items past the completion threshold, order most-recent first,
        /// cap lookups at 32 and visible items at 300.
        /// </summary>
        public static IReadOnlyList<WatchProgress> WindowContinueWatching(
            IEnumerable<WatchProgress> progress,
            DateTimeOffset nowUtc)
        {
            var cutoff = nowUtc.AddDays(-CwDaysCap);
            var candidates = progress
                .Where(p => p != null && !string.IsNullOrEmpty(p.ContentId))
                .Where(p =>
                {
                    var ratio = p.DurationMs <= 0 ? 0 : (double)p.PositionMs / p.DurationMs;
                    return ratio >= WatchProgress.StartedThreshold &&
                           ratio < WatchProgress.CompletedThreshold;
                })
                .Where(p => p.UpdatedAt >= cutoff.ToUnixTimeMilliseconds())
                .OrderByDescending(p => p.UpdatedAt)
                .Take(CwMaxNextUpLookups)
                .ToList();

            return candidates.Take(CwMaxVisibleItems).ToList();
        }

        /// <summary>Render batch size for a given item count (batch + load-ahead).</summary>
        public static int RenderBatchSize(int totalItems)
        {
            if (totalItems <= 0) return 0;
            return Math.Min(totalItems, CwRenderBatchItems + CwRenderLoadAheadItems);
        }

        /// <summary>Row item budget (HOME_MAX_ITEMS_PER_ROW).</summary>
        public static int MaxItemsPerRow() => HomeMaxItemsPerRow;

        /// <summary>
        /// Hero rotation schedule: returns the delay before the next hero slide.
        /// First transition waits 20s; subsequent transitions every 10s.
        /// </summary>
        public static int NextHeroDelayMs(bool isFirstTransition) =>
            isFirstTransition ? HeroRotateFirstDelayMs : HeroRotateIntervalMs;

        /// <summary>Catalog rows to load in the initial pass.</summary>
        public static int InitialCatalogLoad() => HomeInitialCatalogLoad;
    }
}
