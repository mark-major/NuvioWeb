using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Streams
{
    /// <summary>
    /// User-defined stream badge rules. Port of js/core/streams/streamBadgeRules.js:
    /// rule imports (max 3, one active), filter compilation with inline flags
    /// ((?imxs)), candidate-text matching over a stream's names/tags/parsed fields,
    /// and first-wins dedupe by badge key.
    /// </summary>
    public static class StreamBadgeRules
    {
        public const int ImportLimit = 3;

        // ------------------------------------------------------------------
        // Normalization (normalizeStreamBadgeRules)
        // ------------------------------------------------------------------

        public static string NormalizeText(string value)
        {
            if (value == null)
            {
                return "";
            }
            return Regex.Replace(value, @"\s+", " ").Trim();
        }

        // JS parity normalizeColor: transparent/rgba() passthrough; #rgb6/#rgb8 with
        // alpha folding to #rrggbb, rgba(...) or transparent.
        public static string NormalizeColor(string value)
        {
            var text = NormalizeText(value);
            if (Regex.IsMatch(text, @"^(transparent|rgba?\([\d\s,%.]+\))$", RegexOptions.IgnoreCase))
            {
                return text;
            }
            var hex = text.StartsWith("#") ? text.Substring(1) : text;
            if (!Regex.IsMatch(hex, @"^[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
            {
                return "";
            }
            if (hex.Length == 6)
            {
                return "#" + hex.ToUpperInvariant();
            }
            var alpha = Convert.ToInt32(hex.Substring(0, 2), 16);
            var red = Convert.ToInt32(hex.Substring(2, 2), 16);
            var green = Convert.ToInt32(hex.Substring(4, 2), 16);
            var blue = Convert.ToInt32(hex.Substring(6, 2), 16);
            if (alpha >= 255)
            {
                return "#" + hex.Substring(2).ToUpperInvariant();
            }
            if (alpha <= 0)
            {
                return "transparent";
            }
            var alphaText = ((double)alpha / 255).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"rgba({red}, {green}, {blue}, {alphaText})";
        }

        private static JsonElement Prop(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                ? value
                : default;
        }

        private static string Str(JsonElement element, string name)
        {
            var prop = Prop(element, name);
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        private static bool? BoolProp(JsonElement element, string name)
        {
            var prop = Prop(element, name);
            return prop.ValueKind == JsonValueKind.True ? true :
                   prop.ValueKind == JsonValueKind.False ? (bool?)false : null;
        }

        private static List<JsonElement> ArrayOrEmpty(JsonElement element, string name)
        {
            var prop = Prop(element, name);
            var items = new List<JsonElement>();
            if (prop.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prop.EnumerateArray())
                {
                    items.Add(item);
                }
            }
            return items;
        }

        private static BadgeFilter NormalizeFilter(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var name = NormalizeText(Str(source, "name"));
            var pattern = NormalizeText(Str(source, "pattern"));
            if (name.Length == 0 || pattern.Length == 0)
            {
                return null;
            }
            return new BadgeFilter(
                NormalizeText(Str(source, "id")),
                NormalizeText(Str(source, "groupId")),
                name,
                pattern,
                NormalizeText(Str(source, "imageURL")),
                BoolProp(source, "isEnabled") ?? true,
                NormalizeColor(Str(source, "tagColor")),
                NormalizeText(Str(source, "tagStyle")),
                NormalizeColor(Str(source, "textColor")),
                NormalizeColor(Str(source, "borderColor")));
        }

        private static BadgeGroup NormalizeGroup(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new BadgeGroup(
                NormalizeText(Str(source, "id")),
                NormalizeText(Str(source, "name")),
                NormalizeColor(Str(source, "color")),
                BoolProp(source, "isExpanded") ?? true);
        }

        private static BadgeImport NormalizeImport(JsonElement source)
        {
            if (source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var sourceUrl = NormalizeText(Str(source, "sourceUrl"));
            var filters = ArrayOrEmpty(source, "filters")
                .Select(NormalizeFilter)
                .Where(f => f != null)
                .ToList();
            var groups = ArrayOrEmpty(source, "groups")
                .Select(NormalizeGroup)
                .Where(g => g != null)
                .ToList();
            if (sourceUrl.Length == 0 || filters.Count == 0)
            {
                return null;
            }
            var isActive = (BoolProp(source, "isActive") ?? true) && (BoolProp(source, "active") ?? true);
            return new BadgeImport(sourceUrl, filters, groups, isActive);
        }

        public static NormalizedBadgeRules NormalizeStreamBadgeRules(JsonElement value)
        {
            var normalizedImports = new List<BadgeImport>();
            var imports = ArrayOrEmpty(value, "imports");
            foreach (var entry in imports)
            {
                var normalized = NormalizeImport(entry);
                if (normalized == null)
                {
                    continue;
                }
                var existingIndex = normalizedImports.FindIndex(i =>
                    i.SourceUrl.ToLowerInvariant() == normalized.SourceUrl.ToLowerInvariant());
                if (existingIndex >= 0)
                {
                    normalizedImports[existingIndex] = normalized;
                }
                else if (normalizedImports.Count < ImportLimit)
                {
                    normalizedImports.Add(normalized);
                }
            }

            if (normalizedImports.Count == 0)
            {
                return new NormalizedBadgeRules(new List<BadgeImport>());
            }

            var activeIndex = normalizedImports.FindIndex(i => i.IsActive);
            var resolvedActiveIndex = activeIndex >= 0 ? activeIndex : 0;
            var final = normalizedImports
                .Select((item, index) => item.WithActive(index == resolvedActiveIndex))
                .ToList();
            return new NormalizedBadgeRules(final);
        }

        // ------------------------------------------------------------------
        // Compilation + matching
        // ------------------------------------------------------------------

        internal static IReadOnlyList<CompiledBadge> CompileStreamBadgeFilters(NormalizedBadgeRules rules)
        {
            var compiled = new List<CompiledBadge>();
            foreach (var import in rules.Imports.Where(i => i.IsActive))
            {
                foreach (var filter in import.Filters)
                {
                    try
                    {
                        var pattern = (filter.Pattern ?? "").Trim();
                        var flags = RegexOptions.None;
                        // JS parity: strip leading inline (?imxs) groups, fold into flags.
                        var inlineMatch = Regex.Match(pattern, @"^\(\?([imxs]+)\)", RegexOptions.IgnoreCase);
                        while (inlineMatch.Success)
                        {
                            var inlineFlags = inlineMatch.Groups[1].Value.ToLowerInvariant();
                            if (inlineFlags.Contains("i"))
                            {
                                flags |= RegexOptions.IgnoreCase;
                            }
                            if (inlineFlags.Contains("m"))
                            {
                                flags |= RegexOptions.Multiline;
                            }
                            if (inlineFlags.Contains("s"))
                            {
                                flags |= RegexOptions.Singleline;
                            }
                            pattern = pattern.Substring(inlineMatch.Length);
                            inlineMatch = Regex.Match(pattern, @"^\(\?([imxs]+)\)", RegexOptions.IgnoreCase);
                        }
                        compiled.Add(new CompiledBadge(
                            filter.Name,
                            new StreamBadge(
                                filter.Name, filter.ImageURL, filter.TagColor,
                                filter.TagStyle, filter.TextColor, filter.BorderColor),
                            new Regex(pattern, flags)));
                    }
                    catch (ArgumentException)
                    {
                        // Invalid pattern → skipped like the JS catch.
                    }
                }
            }
            return compiled;
        }

        // JS parity badgeMatchCandidates: every text field that can carry a match,
        // newline-split, trimmed, deduped case-insensitively, plus the joined blob.
        internal static IReadOnlyList<string> BadgeMatchCandidates(JsonElement stream)
        {
            var resolve = stream.Prop("clientResolve");
            if (!resolve.IsObject())
            {
                resolve = stream.Prop("raw").Prop("clientResolve");
            }
            var raw = Prop(resolve, "stream").Prop("raw");
            if (!raw.IsObject())
            {
                raw = stream.Prop("raw");
            }
            var parsed = Prop(raw, "parsed");
            var presentation = stream.Prop("streamPresentation");
            if (!presentation.IsObject())
            {
                presentation = stream.Prop("raw").Prop("streamPresentation");
            }
            var behaviorHints = stream.Prop("behaviorHints");
            var debridCacheStatus = stream.Prop("debridCacheStatus");

            var values = new List<object>
            {
                raw.Str("filename"),
                resolve.Str("filename"),
                behaviorHints.Str("filename"),
                debridCacheStatus.Str("cachedName"),
                raw.Str("torrentName"),
                resolve.Str("torrentName"),
                stream.Str("name"),
                stream.Str("title"),
                stream.Str("description"),
                stream.Str("addonName"),
                stream.Str("addonLogo"),
                stream.Str("sourceType"),
                stream.Str("quality"),
                presentation.Str("resolution"),
                presentation.Str("quality"),
                presentation.Str("encode"),
                StringList(presentation, "visualTags"),
                StringList(presentation, "audioTags"),
                StringList(presentation, "audioChannels"),
                StringList(presentation, "languages"),
                parsed.Str("rawTitle"),
                parsed.Str("parsedTitle"),
                parsed.Str("resolution"),
                parsed.Str("quality"),
                parsed.Str("codec"),
                parsed.Str("edition"),
                parsed.Str("group"),
                StringList(parsed, "audio"),
                StringList(parsed, "channels"),
                StringList(parsed, "hdr")
            };

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                // JS String(array) comma-joins; ToString() would print the type name.
                var text = value is System.Collections.IEnumerable enumerable && !(value is string)
                    ? string.Join(",", enumerable.Cast<object>().Select(v => v?.ToString() ?? ""))
                    : value?.ToString();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                foreach (var line in SplitLines(text))
                {
                    var normalized = NormalizeText(line);
                    if (normalized.Length == 0 || !seen.Add(normalized))
                    {
                        continue;
                    }
                    candidates.Add(normalized);
                }
            }
            if (candidates.Count <= 1)
            {
                return candidates;
            }
            candidates.Add(string.Join(" ", candidates));
            return candidates;
        }

        private static string[] SplitLines(string value)
        {
            return value.Replace("\r\n", "\n").Split('\n');
        }

        private static string[] StringList(JsonElement element, string name)
        {
            var prop = Prop(element, name);
            if (prop.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }
            return prop.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .ToArray();
        }

        private static string BadgeDedupeKey(StreamBadge badge)
        {
            // JS parity: imageURL || name — empty string is falsy, unlike C# ??.
            var primary = string.IsNullOrEmpty(badge.ImageURL) ? badge.Name : badge.ImageURL;
            return NormalizeText(primary).ToLowerInvariant();
        }

        public static IReadOnlyList<StreamBadge> MatchStreamBadges(JsonElement stream, NormalizedBadgeRules rules)
        {
            var filters = CompileStreamBadgeFilters(rules);
            if (filters.Count == 0)
            {
                return Array.Empty<StreamBadge>();
            }
            var candidates = BadgeMatchCandidates(stream);
            if (candidates.Count == 0)
            {
                return Array.Empty<StreamBadge>();
            }

            var matched = new Dictionary<string, StreamBadge>(StringComparer.Ordinal);
            foreach (var filter in filters)
            {
                if (candidates.Any(candidate => filter.Regex.IsMatch(candidate)))
                {
                    var key = BadgeDedupeKey(filter.Badge);
                    if (key.Length > 0 && !matched.ContainsKey(key))
                    {
                        matched[key] = filter.Badge;
                    }
                }
            }
            return matched.Values.ToList();
        }

        // JS parity mergeStreamBadges: existing badges keep priority, dedupe by key.
        public static IReadOnlyList<StreamBadge> MergeStreamBadges(
            IEnumerable<StreamBadge> existing, IEnumerable<StreamBadge> matched)
        {
            var merged = new Dictionary<string, StreamBadge>(StringComparer.Ordinal);
            foreach (var badge in (existing ?? Array.Empty<StreamBadge>()).Concat(matched ?? Array.Empty<StreamBadge>()))
            {
                if (badge == null)
                {
                    continue;
                }
                var key = BadgeDedupeKey(badge);
                if (key.Length == 0 || merged.ContainsKey(key))
                {
                    continue;
                }
                merged[key] = badge;
            }
            return merged.Values.ToList();
        }
    }

    // ------------------------------------------------------------------
    // Data holders
    // ------------------------------------------------------------------

    public sealed class BadgeFilter
    {
        public string Id { get; }
        public string GroupId { get; }
        public string Name { get; }
        public string Pattern { get; }
        public string ImageURL { get; }
        public bool IsEnabled { get; }
        public string TagColor { get; }
        public string TagStyle { get; }
        public string TextColor { get; }
        public string BorderColor { get; }

        public BadgeFilter(string id, string groupId, string name, string pattern, string imageURL,
            bool isEnabled, string tagColor, string tagStyle, string textColor, string borderColor)
        {
            Id = id;
            GroupId = groupId;
            Name = name;
            Pattern = pattern;
            ImageURL = imageURL;
            IsEnabled = isEnabled;
            TagColor = tagColor;
            TagStyle = tagStyle;
            TextColor = textColor;
            BorderColor = borderColor;
        }
    }

    public sealed class BadgeGroup
    {
        public string Id { get; }
        public string Name { get; }
        public string Color { get; }
        public bool IsExpanded { get; }

        public BadgeGroup(string id, string name, string color, bool isExpanded)
        {
            Id = id;
            Name = name;
            Color = color;
            IsExpanded = isExpanded;
        }
    }

    public sealed class BadgeImport
    {
        public string SourceUrl { get; }
        public IReadOnlyList<BadgeFilter> Filters { get; }
        public IReadOnlyList<BadgeGroup> Groups { get; }
        public bool IsActive { get; }

        public BadgeImport(string sourceUrl, IReadOnlyList<BadgeFilter> filters,
            IReadOnlyList<BadgeGroup> groups, bool isActive)
        {
            SourceUrl = sourceUrl;
            Filters = filters;
            Groups = groups;
            IsActive = isActive;
        }

        public BadgeImport WithActive(bool isActive)
        {
            return new BadgeImport(SourceUrl, Filters, Groups, isActive);
        }
    }

    public sealed class NormalizedBadgeRules
    {
        public IReadOnlyList<BadgeImport> Imports { get; }

        public NormalizedBadgeRules(IReadOnlyList<BadgeImport> imports)
        {
            Imports = imports;
        }
    }

    public sealed class StreamBadge
    {
        public string Name { get; }
        public string ImageURL { get; }
        public string TagColor { get; }
        public string TagStyle { get; }
        public string TextColor { get; }
        public string BorderColor { get; }

        public StreamBadge(string name, string imageURL, string tagColor, string tagStyle,
            string textColor, string borderColor)
        {
            Name = name;
            ImageURL = imageURL;
            TagColor = tagColor;
            TagStyle = tagStyle;
            TextColor = textColor;
            BorderColor = borderColor;
        }
    }

    internal sealed class CompiledBadge
    {
        public string Name { get; }
        public StreamBadge Badge { get; }
        public Regex Regex { get; }

        public CompiledBadge(string name, StreamBadge badge, Regex regex)
        {
            Name = name;
            Badge = badge;
            Regex = regex;
        }
    }
}
