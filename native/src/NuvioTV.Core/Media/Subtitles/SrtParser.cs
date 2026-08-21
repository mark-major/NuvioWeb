using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Media
{
    /// <summary>A timed text cue (SRT/VTT/ASS normalized shape).</summary>
    public sealed class SubtitleCue
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public string Text { get; set; } = "";

        // ASS alignment (1..9 numpad) preserved for CueLayout; 0 = default.
        public int Alignment { get; set; }
    }

    /// <summary>
    /// SRT parser (subtitleCueLayout.js parity): index lines optional,
    /// "HH:MM:SS,mmm --> HH:MM:SS,mmm" timing.
    /// </summary>
    public static class SrtParser
    {
        internal static readonly Regex TimeRegex = new Regex(
            @"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})",
            RegexOptions.Compiled);

        public static IReadOnlyList<SubtitleCue> Parse(string content)
        {
            var cues = new List<SubtitleCue>();
            if (string.IsNullOrEmpty(content)) return cues;

            foreach (var block in SplitBlocks(content))
            {
                var lines = block.Split('\n');
                var timeIndex = Array.FindIndex(lines, l => TimeRegex.IsMatch(l));
                if (timeIndex < 0) continue;

                var match = TimeRegex.Match(lines[timeIndex]);
                cues.Add(new SubtitleCue
                {
                    StartMs = ToMillis(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value),
                    EndMs = ToMillis(match.Groups[5].Value, match.Groups[6].Value, match.Groups[7].Value, match.Groups[8].Value),
                    Text = string.Join("\n", CollectText(lines, timeIndex + 1))
                });
            }
            return cues;
        }

        internal static IEnumerable<string> SplitBlocks(string content) =>
            content.Replace("\r\n", "\n")
                .Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        internal static long ToMillis(string h, string m, string s, string ms)
        {
            return long.Parse(h) * 3600000
                   + long.Parse(m) * 60000
                   + long.Parse(s) * 1000
                   + PadMillis(ms);
        }

        private static long PadMillis(string value) =>
            long.Parse(value.Length == 1 ? value + "00" : value.Length == 2 ? value + "0" : value);

        private static IEnumerable<string> CollectText(string[] lines, int start)
        {
            for (var i = start; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }
    }
}
