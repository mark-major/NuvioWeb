using System;
using System.Collections.Generic;
using System.Linq;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// Storage capacity limits and enforcement rules.
    /// </summary>
    public static class StorageCaps
    {
        // Individual collection caps
        private const int WatchProgressCap = 5000;
        private const int WatchedItemsCap = 5000;
        private const int SavedLibraryCap = 1000;
        private const int StreamPreferencesCap = 500;

        // Home image cache: 500 items, 30-day retention
        private const int HomeImageCacheCap = 500;
        private static readonly TimeSpan HomeImageCacheMaxAge = TimeSpan.FromDays(30);

        /// <summary>
        /// Enforces a cap on a collection, keeping the newest items based on the selector.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">The collection to cap.</param>
        /// <param name="cap">The maximum number of items to keep.</param>
        /// <param name="updatedAtSelector">Function to extract the update timestamp from an item.</param>
        /// <returns>The capped collection, keeping the newest items.</returns>
        public static IEnumerable<T> Enforce<T>(IEnumerable<T> items, int cap, Func<T, DateTime> updatedAtSelector)
        {
            if (items == null)
            {
                return Enumerable.Empty<T>();
            }

            var itemList = items.ToList();
            if (itemList.Count <= cap)
            {
                return itemList;
            }

            // Sort by updated date descending (newest first) and take top cap items
            return itemList
                .OrderByDescending(updatedAtSelector)
                .Take(cap);
        }

        /// <summary>
        /// Enforces the watch history cap (5000 items, keep newest).
        /// </summary>
        public static IEnumerable<T> EnforceWatchProgress<T>(IEnumerable<T> items, Func<T, DateTime> updatedAtSelector)
        {
            return Enforce(items, WatchProgressCap, updatedAtSelector);
        }

        /// <summary>
        /// Enforces the watched items cap (5000 items, keep newest).
        /// </summary>
        public static IEnumerable<T> EnforceWatchedItems<T>(IEnumerable<T> items, Func<T, DateTime> updatedAtSelector)
        {
            return Enforce(items, WatchedItemsCap, updatedAtSelector);
        }

        /// <summary>
        /// Enforces the saved library cap (1000 items, keep newest).
        /// </summary>
        public static IEnumerable<T> EnforceSavedLibrary<T>(IEnumerable<T> items, Func<T, DateTime> updatedAtSelector)
        {
            return Enforce(items, SavedLibraryCap, updatedAtSelector);
        }

        /// <summary>
        /// Enforces the stream preferences cap (500 items, keep newest).
        /// </summary>
        public static IEnumerable<T> EnforceStreamPreferences<T>(IEnumerable<T> items, Func<T, DateTime> updatedAtSelector)
        {
            return Enforce(items, StreamPreferencesCap, updatedAtSelector);
        }

        /// <summary>
        /// Enforces the home image cache rules: 500 items max, 30-day retention.
        /// Keeps items that are within 30 days OR among the 500 newest items.
        /// </summary>
        public static IEnumerable<T> EnforceHomeImageCache<T>(IEnumerable<T> items, Func<T, DateTime> updatedAtSelector)
        {
            if (items == null)
            {
                return Enumerable.Empty<T>();
            }

            var itemList = items.ToList();
            var now = DateTime.UtcNow;
            var thirtyDaysAgo = now.AddDays(-30);

            // Keep items within 30 days, cap at 500, sort by UpdatedAt descending (newest first)
            return itemList
                .Where(item => updatedAtSelector(item) >= thirtyDaysAgo)
                .OrderByDescending(updatedAtSelector)
                .Take(HomeImageCacheCap);
        }

        /// <summary>
        /// Enforces the home image cache rules using a default UpdatedAt property.
        /// For items with an UpdatedAt DateTime property.
        /// </summary>
        public static IEnumerable<T> EnforceHomeImageCache<T>(IEnumerable<T> items) where T : IHasUpdatedAt
        {
            return EnforceHomeImageCache(items, item => item.UpdatedAt);
        }
    }

    /// <summary>
    /// Interface for items with an UpdatedAt timestamp.
    /// Used for storage cap enforcement.
    /// </summary>
    public interface IHasUpdatedAt
    {
        DateTime UpdatedAt { get; set; }
    }
}
