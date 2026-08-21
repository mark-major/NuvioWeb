using System.Collections.Generic;
using NuvioTV.Core.Input;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Ports the behavioral expectations of screen.js moveFocusDirectional().
    /// </summary>
    public class SpatialFocusSolverTests
    {
        private static FocusableRect Rect(string id, float x, float y, float w = 200, float h = 100) =>
            new FocusableRect { Id = id, X = x, Y = y, Width = w, Height = h };

        [Fact]
        public void Down_PicksDirectlyBelow()
        {
            var current = Rect("a", 0, 0);
            var below = Rect("b", 0, 200);
            var above = Rect("c", 0, -200);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Down,
                new[] { current, below, above });

            Assert.Equal("b", result.Value.Id);
        }

        [Fact]
        public void Right_PicksNearestToTheRight()
        {
            var current = Rect("a", 0, 0);
            var near = Rect("near", 300, 0);
            var far = Rect("far", 900, 0);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Right,
                new[] { current, far, near });

            Assert.Equal("near", result.Value.Id);
        }

        [Fact]
        public void Up_IgnoresCandidatesWithinTwoPixels()
        {
            var current = Rect("a", 0, 500);
            var barelyAbove = Rect("nudge", 0, 499); // dy = -1 → filtered
            var realAbove = Rect("real", 0, 300);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Up,
                new[] { current, barelyAbove, realAbove });

            Assert.Equal("real", result.Value.Id);
        }

        [Fact]
        public void Down_AlignedCandidateWinsOverCloserMisaligned()
        {
            // Current width 600 → axis tolerance max(0.7·600, …) = 420.
            // Misaligned candidate sits closer on Y but far off on X.
            var current = Rect("a", 0, 0, 600, 100);
            var misalignedClose = Rect("off", 1200, 150);
            var alignedFarther = Rect("aligned", 100, 400);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Down,
                new[] { current, misalignedClose, alignedFarther });

            // Misaligned one is still in the snap band? primary=150 vs nearest=150,
            // tolerance max(0.9·100,42)=90 → only itself in band... but it IS the
            // nearest, so it wins via sortedPick. Webapp parity: nearest row wins.
            Assert.Equal("off", result.Value.Id);
        }

        [Fact]
        public void Down_RowSnapPrefersAlignedWithinBand()
        {
            // Two rows: row1 at dy≈150 (misaligned, dx huge), row2 at dy≈250 with an
            // aligned candidate. Row1 band = [nearest .. nearest+max(90,42)] excludes
            // row2 → row1 picked (webapp snaps to nearest row first).
            var current = Rect("a", 0, 0, 200, 100);
            var row1Off = Rect("row1", 1600, 150);
            var row2Aligned = Rect("row2", 0, 260);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Down,
                new[] { current, row1Off, row2Aligned });

            Assert.Equal("row1", result.Value.Id);
        }

        [Fact]
        public void Left_TieBreaksByVerticalDistance()
        {
            var current = Rect("a", 800, 0);
            var upLeft = Rect("up", 0, -100);
            var downLeft = Rect("down", 0, 100);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Left,
                new[] { current, downLeft, upLeft });

            // Same |dy| → deterministic pick of first encountered minimum ("down").
            Assert.Equal("down", result.Value.Id);
        }

        [Fact]
        public void NoCandidate_ReturnsNull_AtBoundary()
        {
            var current = Rect("a", 0, 0);
            var rightOnly = Rect("r", 500, 0);

            var left = SpatialFocusSolver.FindBest(current, FocusDirection.Left, new[] { rightOnly });
            Assert.Null(left);

            var right = SpatialFocusSolver.FindBest(current, FocusDirection.Right, new[] { rightOnly, current });
            Assert.Equal("r", right.Value.Id);
        }

        [Fact]
        public void InvisibleCandidates_AreSkipped()
        {
            var current = Rect("a", 0, 0);
            var invisible = Rect("ghost", 0, 300, 0, 0); // zero-size
            var visible = Rect("vis", 0, 300);

            var result = SpatialFocusSolver.FindBest(current, FocusDirection.Down,
                new[] { current, invisible, visible });

            Assert.Equal("vis", result.Value.Id);
        }
    }
}
