using System;
using System.Collections.Generic;
using System.Linq;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// Port of subtitleCueLayout.js: ASS \an1-9 alignment → line/align
    /// placement; VTT default = bottom center. Also provides cue lookup by
    /// position for the renderer's position-polling clock.
    /// </summary>
    public static class CueLayout
    {
        public sealed class Layout
        {
            /// <summary>"top" | "middle" | "bottom"</summary>
            public string Vertical;
            /// <summary>"left" | "center" | "right"</summary>
            public string Align;
        }

        public static Layout FromAlignment(int alignment)
        {
            switch (alignment)
            {
                case 1: return new Layout { Vertical = "bottom", Align = "left" };
                case 2: return new Layout { Vertical = "bottom", Align = "center" };
                case 3: return new Layout { Vertical = "bottom", Align = "right" };
                case 4: return new Layout { Vertical = "middle", Align = "left" };
                case 5: return new Layout { Vertical = "middle", Align = "center" };
                case 6: return new Layout { Vertical = "middle", Align = "right" };
                case 7: return new Layout { Vertical = "top", Align = "left" };
                case 8: return new Layout { Vertical = "top", Align = "center" };
                case 9: return new Layout { Vertical = "top", Align = "right" };
                default:
                    return new Layout { Vertical = "bottom", Align = "center" }; // SRT/VTT default
            }
        }

        /// <summary>Returns cues active at the given position (end inclusive).</summary>
        public static IReadOnlyList<SubtitleCue> ActiveAt(
            IEnumerable<SubtitleCue> cues, long positionMs) =>
            cues.Where(c => positionMs >= c.StartMs && positionMs < c.EndMs).ToList();

        /// <summary>
        /// subtitleVerticalOffset port: clamps to −20..50 in steps of 5.
        /// </summary>
        public static int ClampVerticalOffset(int offset)
        {
            var stepped = (int)Math.Round(offset / 5.0, MidpointRounding.AwayFromZero) * 5;
            return Math.Max(-20, Math.Min(50, stepped));
        }
    }
}
