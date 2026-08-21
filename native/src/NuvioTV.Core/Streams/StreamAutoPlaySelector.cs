using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Streams
{
    public enum StreamAutoPlayMode
    {
        Manual,
        FirstStream,
        RegexMatch
    }

    public enum StreamAutoPlaySource
    {
        AllSources,
        InstalledAddonsOnly,
        EnabledPluginsOnly
    }

    /// <summary>
    /// Auto stream selection. Port of js/core/streams/streamAutoPlaySelector.js
    /// (itself a port of the Android TV StreamAutoPlaySelector): mode/source
    /// normalization, source scoping, addon/plugin selection filters, binge-group
    /// priority, and regex matching with negative-lookahead exclusion words.
    /// Streams are JsonElement objects shaped like the webapp's picker entries.
    /// </summary>
    public static class StreamAutoPlaySelector
    {
        public static StreamAutoPlayMode NormalizeMode(string value)
        {
            switch ((value ?? "").Trim().ToUpperInvariant())
            {
                case "FIRST_STREAM": return StreamAutoPlayMode.FirstStream;
                case "REGEX_MATCH": return StreamAutoPlayMode.RegexMatch;
                default: return StreamAutoPlayMode.Manual;
            }
        }

        public static StreamAutoPlaySource NormalizeSource(string value)
        {
            switch ((value ?? "").Trim().ToUpperInvariant())
            {
                case "INSTALLED_ADDONS_ONLY": return StreamAutoPlaySource.InstalledAddonsOnly;
                case "ENABLED_PLUGINS_ONLY": return StreamAutoPlaySource.EnabledPluginsOnly;
                default: return StreamAutoPlaySource.AllSources;
            }
        }

        public static bool IsRegexSelectionConfigured(string regexPattern)
        {
            var pattern = (regexPattern ?? "").Trim();
            if (pattern.Length == 0 || !Regex.IsMatch(pattern, "[a-z0-9]", RegexOptions.IgnoreCase))
            {
                return false;
            }
            try
            {
                new Regex(pattern, RegexOptions.IgnoreCase);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static bool IsAutoPlayEffectivelyEnabled(string mode, string regexPattern)
        {
            switch (NormalizeMode(mode))
            {
                case StreamAutoPlayMode.FirstStream:
                    return true;
                case StreamAutoPlayMode.RegexMatch:
                    return IsRegexSelectionConfigured(regexPattern);
                default:
                    return false;
            }
        }

        // JS parity isPlayableStream: debrid checking states block; something playable
        // must exist so auto-play never lands on an external web page.
        private static bool IsPlayableStream(JsonElement stream)
        {
            var resolve = stream.Prop("clientResolve");
            if (!resolve.IsObject())
            {
                resolve = stream.Prop("raw").Prop("clientResolve");
            }
            var debridState = (stream.Prop("debridCacheStatus").Str("state") ?? "").Trim().ToUpperInvariant();
            if (debridState == "CHECKING" || debridState == "NOT_CACHED" || debridState == "UNKNOWN")
            {
                return false;
            }
            return stream.IsObject() &&
                   (stream.Prop("url").ValueKind != JsonValueKind.Undefined ||
                    stream.Prop("ytId").ValueKind != JsonValueKind.Undefined ||
                    stream.Prop("infoHash").ValueKind != JsonValueKind.Undefined ||
                    resolve.Prop("infoHash").ValueKind != JsonValueKind.Undefined ||
                    resolve.Prop("magnetUri").ValueKind != JsonValueKind.Undefined ||
                    stream.Prop("engineFs").ValueKind != JsonValueKind.Undefined ||
                    stream.Prop("tizenP2p").ValueKind != JsonValueKind.Undefined ||
                    stream.Prop("debridCacheStatus").ValueKind == JsonValueKind.Object);
        }

        private static string StreamBingeGroup(JsonElement stream)
        {
            var hints = stream.Prop("behaviorHints");
            if (!hints.IsObject())
            {
                hints = stream.Prop("raw").Prop("behaviorHints");
            }
            return (hints.Str("bingeGroup") ?? "").Trim();
        }

        private static string StreamSearchableText(JsonElement stream)
        {
            return string.Join(" ",
                stream.Str("addonName"),
                stream.Str("name"),
                stream.Str("title"),
                stream.Str("description"),
                stream.Str("url"),
                stream.Str("infoHash"));
        }

        // JS parity buildExcludeRegex: extract words from negative lookaheads like
        // (?!.*(CAM|TS)) so one pattern can include and exclude.
        internal static Regex BuildExcludeRegex(string pattern)
        {
            var matches = Regex.Matches(pattern ?? "", @"\(\?![^)]*?\(([^)]+)\)");
            var words = new List<string>();
            foreach (Match m in matches)
            {
                words.Add(m.Groups[1].Value);
            }
            var splitWords = words
                .SelectMany(w => w.Split('|'))
                .Select(w => w.Trim())
                .Where(w => w.Length > 0)
                .Distinct()
                .ToList();
            if (splitWords.Count == 0)
            {
                return null;
            }
            try
            {
                return new Regex(@"\b(" + string.Join("|", splitWords) + @")\b", RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static JsonElement? SelectAutoPlayStream(
            IReadOnlyList<JsonElement> streams,
            string mode = null,
            string source = null,
            IEnumerable<string> installedAddonNames = null,
            IEnumerable<string> selectedAddons = null,
            IEnumerable<string> selectedPlugins = null,
            string preferredBingeGroup = null,
            bool preferBingeGroupInSelection = false,
            bool bingeGroupOnly = false,
            string regexPattern = null)
        {
            var list = (streams ?? Array.Empty<JsonElement>()).Where(s => s.IsObject()).ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var installed = new HashSet<string>(installedAddonNames ?? Array.Empty<string>());
            var addonsFilter = new HashSet<string>(selectedAddons ?? Array.Empty<string>());
            var pluginsFilter = new HashSet<string>(selectedPlugins ?? Array.Empty<string>());

            // Source scoping (JS scopeStreamsBySource).
            var candidates = list.Where(s =>
            {
                var name = s.Str("addonName") ?? "";
                switch (NormalizeSource(source))
                {
                    case StreamAutoPlaySource.InstalledAddonsOnly:
                        return installed.Contains(name);
                    case StreamAutoPlaySource.EnabledPluginsOnly:
                        return !installed.Contains(name);
                    default:
                        return true;
                }
            }).Where(s =>
            {
                var addonName = s.Str("addonName") ?? "";
                var isAddonStream = installed.Contains(addonName);
                return isAddonStream
                    ? addonsFilter.Count == 0 || addonsFilter.Contains(addonName)
                    : pluginsFilter.Count == 0 || pluginsFilter.Contains(addonName);
            }).ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Binge-group priority applies even in MANUAL (Android parity).
            var preferred = (preferredBingeGroup ?? "").Trim();
            if (preferBingeGroupInSelection && preferred.Length > 0)
            {
                var bingeMatch = candidates.FirstOrDefault(s =>
                    StreamBingeGroup(s) == preferred && IsPlayableStream(s));
                if (bingeMatch.ValueKind == JsonValueKind.Object)
                {
                    return bingeMatch;
                }
                if (bingeGroupOnly)
                {
                    return null;
                }
            }

            var resolvedMode = NormalizeMode(mode);
            if (resolvedMode == StreamAutoPlayMode.Manual)
            {
                return null;
            }

            if (resolvedMode == StreamAutoPlayMode.FirstStream)
            {
                return candidates.FirstOrDefault(IsPlayableStream) is { ValueKind: JsonValueKind.Object } first
                    ? first
                    : (JsonElement?)null;
            }

            // REGEX_MATCH
            var pattern = (regexPattern ?? "").Trim();
            Regex includeRegex;
            try
            {
                includeRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return null;
            }
            var excludeRegex = BuildExcludeRegex(pattern);

            foreach (var candidate in candidates)
            {
                if (!IsPlayableStream(candidate))
                {
                    continue;
                }
                var text = StreamSearchableText(candidate);
                if (!includeRegex.IsMatch(text))
                {
                    continue;
                }
                if (excludeRegex != null && excludeRegex.IsMatch(text))
                {
                    continue;
                }
                return candidate;
            }
            return null;
        }
    }
}
