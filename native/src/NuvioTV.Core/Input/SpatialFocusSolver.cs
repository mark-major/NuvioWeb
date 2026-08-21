using System;
using System.Collections.Generic;

namespace NuvioTV.Core.Input
{
    /// <summary>Axis-aligned focus candidate rectangle in screen pixels.</summary>
    public struct FocusableRect
    {
        public string Id;
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public float CenterX => X + Width / 2f;
        public float CenterY => Y + Height / 2f;
    }

    public enum FocusDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    /// <summary>
    /// Pure port of js/ui/navigation/screen.js moveFocusDirectional(): spatial
    /// dpad focus selection with primary-axis filter (&gt;2px), alignment
    /// tolerance max(0.7·A, 0.7·B, 48), row/column snap tolerance
    /// max(0.9·rect, 42), and score = primary·1000 + secondary ordering.
    /// </summary>
    public static class SpatialFocusSolver
    {
        public static FocusableRect? FindBest(
            FocusableRect current,
            FocusDirection direction,
            IEnumerable<FocusableRect> candidates)
        {
            if (candidates == null) return null;

            var vertical = direction == FocusDirection.Up || direction == FocusDirection.Down;

            // Filter: must move >2px on the primary axis, and not be `current`
            // itself (id equality when provided).
            Entry best = null;
            var nearestPrimary = float.PositiveInfinity;
            var rowCandidates = new List<Entry>();

            foreach (var raw in candidates)
            {
                if (raw.Width <= 0 || raw.Height <= 0) continue; // invisible parity
                if (!string.IsNullOrEmpty(current.Id) && raw.Id == current.Id) continue;

                var dx = raw.CenterX - current.CenterX;
                var dy = raw.CenterY - current.CenterY;

                bool forward;
                switch (direction)
                {
                    case FocusDirection.Up: forward = dy < -2; break;
                    case FocusDirection.Down: forward = dy > 2; break;
                    case FocusDirection.Left: forward = dx < -2; break;
                    default: forward = dx > 2; break;
                }
                if (!forward) continue;

                var primary = vertical ? Math.Abs(dy) : Math.Abs(dx);
                var secondary = vertical ? Math.Abs(dx) : Math.Abs(dy);
                var axisTolerance = vertical
                    ? Math.Max(Math.Max(current.Width * 0.7f, raw.Width * 0.7f), 48f)
                    : Math.Max(Math.Max(current.Height * 0.7f, raw.Height * 0.7f), 48f);
                var aligned = secondary <= axisTolerance;

                var entry = new Entry
                {
                    Rect = raw,
                    Dx = dx,
                    Dy = dy,
                    Primary = primary,
                    Secondary = secondary,
                    Aligned = aligned,
                    Score = primary * 1000f + secondary
                };

                if (primary < nearestPrimary) nearestPrimary = primary;
                rowCandidates.Add(entry);
            }

            if (rowCandidates.Count == 0) return null;

            // Row/column snap: everything within nearestPrimary + max(0.9·rect, 42).
            var snapTolerance = Math.Max(
                (vertical ? current.Height : current.Width) * 0.9f, 42f);
            Entry alignedPick = null;
            Entry sortedPick = null;

            foreach (var e in rowCandidates)
            {
                e.InRow = e.Primary <= nearestPrimary + snapTolerance;
            }

            foreach (var e in rowCandidates)
            {
                if (!e.InRow) continue;
                if (e.Aligned &&
                    (alignedPick == null ||
                     e.Secondary < alignedPick.Secondary ||
                     (e.Secondary == alignedPick.Secondary && e.Primary < alignedPick.Primary)))
                {
                    alignedPick = e;
                }
                if (sortedPick == null ||
                    e.Secondary < sortedPick.Secondary ||
                    (e.Secondary == sortedPick.Secondary && e.Primary < sortedPick.Primary))
                {
                    sortedPick = e;
                }
            }

            best = alignedPick ?? sortedPick;
            return best?.Rect;
        }

        private sealed class Entry
        {
            public FocusableRect Rect;
            public float Dx;
            public float Dy;
            public float Primary;
            public float Secondary;
            public float Score;
            public bool Aligned;
            public bool InRow;
        }
    }
}
