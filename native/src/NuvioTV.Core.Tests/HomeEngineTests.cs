using System;
using System.Collections.Generic;
using System.Linq;
using NuvioTV.Core.Home;
using NuvioTV.Core.Models;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>Host tests for the home data pipeline (Task 13.1).</summary>
    public class HomeEngineTests
    {
        private static WatchProgress Progress(string id, double ratio, long updatedDaysAgo, long nowMs)
        {
            return new WatchProgress(
                id, "movie", null,
                (long)(ratio * 100000), 100000,
                nowMs - updatedDaysAgo * 86400000L);
        }

        [Fact]
        public void WindowContinueWatching_DropsCompletedAndUnstarted()
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var progress = new[]
            {
                Progress("unstarted", 0.001, 0, nowMs),   // below start threshold
                Progress("partial", 0.5, 1, nowMs),        // valid
                Progress("finished", 0.95, 0, nowMs),      // completed → drop
            };

            var result = HomeEngine.WindowContinueWatching(progress, now);
            Assert.Equal(new[] { "partial" }, result.Select(p => p.ContentId));
        }

        [Fact]
        public void WindowContinueWatching_EnforcesSixtyDayCap()
        {
            var now = DateTimeOffset.UtcNow;
            var progress = new[] { Progress("stale", 0.5, 90, now.ToUnixTimeMilliseconds()) };

            Assert.Empty(HomeEngine.WindowContinueWatching(progress, now));
        }

        [Fact]
        public void WindowContinueWatching_OrdersMostRecentFirst()
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var progress = new[]
            {
                Progress("older", 0.4, 5, nowMs),
                Progress("newer", 0.6, 1, nowMs),
            };

            var result = HomeEngine.WindowContinueWatching(progress, now);
            Assert.Equal("newer", result.First().ContentId);
        }

        [Fact]
        public void WindowContinueWatching_CapsLookups()
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            var progress = Enumerable.Range(0, 50)
                .Select(i => Progress($"id{i}", 0.5, 0, nowMs - i)); // staggered timestamps

            var result = HomeEngine.WindowContinueWatching(progress, now);
            Assert.Equal(HomeEngine.CwMaxNextUpLookups, result.Count);
        }

        [Fact]
        public void NextLayout_FollowsModernGridClassicCycle()
        {
            Assert.Equal("grid", HomeEngine.NextLayout("modern"));
            Assert.Equal("classic", HomeEngine.NextLayout("grid"));
            // Unknown layouts resolve to index -1 → next is "modern" (webapp parity).
            Assert.Equal("modern", HomeEngine.NextLayout("bogus"));
        }

        [Fact]
        public void HeroRotation_UsesTwentySecondFirstDelay()
        {
            Assert.Equal(20000, HomeEngine.NextHeroDelayMs(true));
            Assert.Equal(10000, HomeEngine.NextHeroDelayMs(false));
        }
    }
}
