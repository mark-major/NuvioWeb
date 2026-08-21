using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// ASS/SSA parser: Dialogue events with \an1-9 alignment preserved for the
    /// cue layout; inline override tags are stripped from the text.
    /// </summary>
    public static class AssParser
    {
        private static readonly Regex DialogueRegex = new Regex(
            @"^Dialogue:\s*[^,]*,\s*(\d+):(\d{2}):(\d{2})\.(\d{2})\s*,\s*(\d+):(\d{2}):(\d{2})\.(\d{2})\s*,(.*)$",
            RegexOptions.Compiled);

        public static IReadOnlyList<SubtitleCue> Parse(string content)
        {
            var cues = new List<SubtitleCue>();
            if (string.IsNullOrEmpty(content)) return cues;

            foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

                var match = DialogueRegex.Match(line);
                if (!match.Success) continue;

                // Fields after timing: style,name,marginL,marginR,marginV,effect,text
                var tail = match.Groups[9].Value;
                var fields = SplitFields(tail, 7);
                if (fields == null || fields.Count < 7) continue;

                // Alignment comes from an inline {\anN} tag when present;
                // style-based alignment requires the full Format header.
                var alignmentMatch = Regex.Match(fields.Count > 6 ? fields[6] : "", @"\\an([1-9])");
                var alignment = alignmentMatch.Success
                    ? int.Parse(alignmentMatch.Groups[1].Value)
                    : 0;
                var text = string.Join("\n", fields[6]
                    .Split(new[] { "\\N", "\\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => StripOverrideTags(part).Trim())
                    .Where(part => part.Length > 0));
                if (text.Length == 0 && fields[6].Length > 0) continue;

                cues.Add(new SubtitleCue
                {
                    StartMs = ToMillis(match.Groups[1].Value, match.Groups[2].Value,
                        match.Groups[3].Value, match.Groups[4].Value),
                    EndMs = ToMillis(match.Groups[5].Value, match.Groups[6].Value,
                        match.Groups[7].Value, match.Groups[8].Value),
                    Text = text,
                    Alignment = alignment
                });
            }
            return cues;
        }

        /// <summary>Parses \anN alignment from a style or text segment.</summary>
        public static int ParseAlignment(string assAlignment)
        {
            if (int.TryParse(assAlignment?.Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value) &&
                value >= 1 && value <= 9)
            {
                return value;
            }
            return 0;
        }

        private static List<string> SplitFields(string input, int count)
        {
            // Split at the first (count-1) top-level commas; the remainder
            // (which may contain commas inside override tags) is the last field.
            var fields = new List<string>();
            var start = 0;
            var depth = 0;
            for (var i = 0; i < input.Length && fields.Count < count - 1; i++)
            {
                if (input[i] == '{') depth++;
                else if (input[i] == '}') depth = Math.Max(0, depth - 1);
                else if (input[i] == ',' && depth == 0)
                {
                    fields.Add(input.Substring(start, i - start));
                    start = i + 1;
                }
            }
            fields.Add(input.Substring(start));
            return fields;
        }

        internal static string StripOverrideTags(string text) =>
            Regex.Replace(text ?? "", @"\{[^}]*\}", "");

        private static long ToMillis(string h, string mm, string ss, string cs)
        {
            return long.Parse(h) * 3600000
                   + long.Parse(mm) * 60000
                   + long.Parse(ss) * 1000
                   + long.Parse(cs.PadRight(2, '0').Substring(0, 2)) * 10;
        }
    }
}
