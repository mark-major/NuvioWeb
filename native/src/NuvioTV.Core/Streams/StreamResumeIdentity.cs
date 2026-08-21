using System;
using System.Text;
using System.Text.Json;

namespace NuvioTV.Core.Streams
{
    /// <summary>
    /// Shared JSON-property access for the stream logic ports. Streams arrive as
    /// parsed JSON (the webapp passes plain objects); property names are matched
    /// exactly like JS member access.
    /// </summary>
    public static class StreamJson
    {
        public static bool IsObject(this JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object;
        }

        public static JsonElement Prop(this JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value))
            {
                return value;
            }
            return default;
        }

        public static string Str(this JsonElement element, string name)
        {
            var prop = element.Prop(name);
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }

        public static long? Num(this JsonElement element, string name)
        {
            var prop = element.Prop(name);
            return prop.ValueKind == JsonValueKind.Number ? prop.GetInt64() : (long?)null;
        }

        // JS firstNonEmpty: first trimmed non-empty string.
        public static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                var normalized = (value ?? "").Trim();
                if (normalized.Length > 0)
                {
                    return normalized;
                }
            }
            return "";
        }

        // JS normalizeText: trim, collapse whitespace, lowercase.
        public static string NormalizeText(string value)
        {
            var text = value ?? "";
            var sb = new StringBuilder(text.Length);
            var inSpace = false;
            foreach (var ch in text.Trim())
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!inSpace)
                    {
                        sb.Append(' ');
                        inSpace = true;
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    inSpace = false;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Stable resume identity for a stream. Port of
    /// js/core/streams/streamResumeIdentity.js buildStreamResumeIdentity:
    /// torrent identity when an info hash exists, file identity on filename,
    /// else url/magnet locator; owner and provider ride along.
    /// </summary>
    public static class StreamResumeIdentity
    {
        public static string BuildStreamResumeIdentity(JsonElement stream)
        {
            if (!stream.IsObject())
            {
                return "";
            }
            var raw = stream.Prop("raw");
            var resolve = stream.Prop("clientResolve");
            if (!resolve.IsObject())
            {
                resolve = raw.Prop("clientResolve");
            }
            var behaviorHints = stream.Prop("behaviorHints");
            if (!behaviorHints.IsObject())
            {
                behaviorHints = raw.Prop("behaviorHints");
            }
            var origin = stream.Prop("streamOrigin");
            if (!origin.IsObject())
            {
                origin = raw.Prop("streamOrigin");
            }
            var debridCacheStatus = stream.Prop("debridCacheStatus");
            if (!debridCacheStatus.IsObject())
            {
                debridCacheStatus = raw.Prop("debridCacheStatus");
            }

            var owner = StreamJson.NormalizeText(StreamJson.FirstNonEmpty(
                stream.Str("addonId"),
                raw.Str("addonId"),
                origin.Str("addonId"),
                stream.Str("sourceProviderId"),
                raw.Str("sourceProviderId"),
                origin.Str("sourceProviderId"),
                stream.Str("addonBaseUrl"),
                raw.Str("addonBaseUrl"),
                origin.Str("addonBaseUrl"),
                stream.Str("addonName"),
                raw.Str("addonName"),
                origin.Str("addonName")));

            var provider = StreamJson.NormalizeText(StreamJson.FirstNonEmpty(
                resolve.Str("service"),
                debridCacheStatus.Str("providerId"),
                debridCacheStatus.Str("providerId")));

            var fileIdx = StreamJson.FirstNonEmpty(
                ToStringOrNull(resolve.Num("fileIdx")),
                ToStringOrNull(stream.Num("fileIdx")),
                ToStringOrNull(raw.Num("fileIdx")));

            var filename = StreamJson.NormalizeText(StreamJson.FirstNonEmpty(
                behaviorHints.Str("filename"),
                resolve.Str("filename"),
                raw.Str("filename")));

            var infoHash = StreamJson.NormalizeText(StreamJson.FirstNonEmpty(
                stream.Str("infoHash"),
                raw.Str("infoHash"),
                resolve.Str("infoHash")));

            if (infoHash.Length > 0)
            {
                return IdentityPart("torrent", new[] { owner, provider, infoHash, fileIdx, filename });
            }
            if (filename.Length > 0)
            {
                return IdentityPart("file", new[] { owner, provider, filename, fileIdx });
            }

            var locator = StreamJson.FirstNonEmpty(
                stream.Str("url"),
                stream.Str("externalUrl"),
                stream.Str("ytId"),
                raw.Str("url"),
                raw.Str("externalUrl"),
                raw.Str("ytId"),
                resolve.Str("magnetUri"));
            if (locator.Length == 0)
            {
                return "";
            }
            var kind = locator.Trim().ToLowerInvariant().StartsWith("magnet:") ? "magnet" : "url";
            return IdentityPart(kind, new[] { owner, provider, locator, fileIdx });
        }

        private static string ToStringOrNull(long? value)
        {
            return value.HasValue ? value.Value.ToString() : null;
        }

        private static string IdentityPart(string kind, string[] values)
        {
            var sb = new StringBuilder(kind.Length + 8);
            sb.Append(kind).Append(':').Append('[');
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(JsonSerializerSerialize(values[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonSerializerSerialize(string value)
        {
            return System.Text.Json.JsonSerializer.Serialize(value ?? "");
        }
    }
}
