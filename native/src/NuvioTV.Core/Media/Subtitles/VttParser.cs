using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// WebVTT parser: WEBVTT header, cue timings with optional settings line,
    /// basic markup stripping (&lt;b&gt;, &lt;i&gt;, &lt;u&gt;, &lt;v Name&gt;).
    /// </summary>
    public static class VttParser
    {
        private static readonly Regex TimeRegex = new Regex(
            @"(\d{1,2}):(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})\.(\d{3})",
            RegexOptions.Compiled);

        private static readonly Regex ShortTimeRegex = new Regex(
            @"(?:^|\s)(\d{1,2}):(\d{2})\.(\d{3})\s*-->\s*(\d{1,2}):(\d{2})\.(\d{3})",
            RegexOptions.Compiled);

        private static readonly Regex MarkupRegex =
            new Regex(@"<[^>]+>", RegexOptions.Compiled);

        public static IReadOnlyList<SubtitleCue> Parse(string content)
        {
            var cues = new List<SubtitleCue>();
            if (string.IsNullOrEmpty(content)) return cues;

            foreach (var block in SrtParser.SplitBlocks(content))
            {
                if (block.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal) ||
                    block.TrimStart().StartsWith("NOTE", StringComparison.Ordinal) ||
                    block.TrimStart().StartsWith("STYLE", StringComparison.Ordinal) ||
                    block.TrimStart().StartsWith("REGION", StringComparison.Ordinal))
                {
                    continue;
                }

                var lines = block.Split('\n');
                var timeIndex = Array.FindIndex(lines, IsTimeLine);
                if (timeIndex < 0) continue;

                long start, end;
                if (TimeRegex.IsMatch(lines[timeIndex]))
                {
                    var m = TimeRegex.Match(lines[timeIndex]);
                    start = SrtParser.ToMillis(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
                    end = SrtParser.ToMillis(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
                }
                else
                {
                    // MM:SS.mmm form (no hours).
                    var m = ShortTimeRegex.Match(lines[timeIndex]);
                    start = long.Parse(m.Groups[1].Value) * 60000 + long.Parse(m.Groups[2].Value) * 1000
                            + long.Parse(m.Groups[3].Value);
                    end = long.Parse(m.Groups[4].Value) * 60000 + long.Parse(m.Groups[5].Value) * 1000
                          + long.Parse(m.Groups[6].Value);
                }

                cues.Add(new SubtitleCue
                {
                    StartMs = start,
                    EndMs = end,
                    Text = string.Join("\n", lines.Skip(timeIndex + 1)
                        .Select(l => StripMarkup(l.Trim()))
                        .Where(l => l.Length > 0))
                });
            }
            return cues;
        }

        private static bool IsTimeLine(string line) =>
            TimeRegex.IsMatch(line) || ShortTimeRegex.IsMatch(line);

        internal static string StripMarkup(string line) =>
            MarkupRegex.Replace(line ?? "", "").Trim();
    }
}
