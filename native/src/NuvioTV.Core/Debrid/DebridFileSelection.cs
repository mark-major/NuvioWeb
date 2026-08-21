using System;
using System.Collections.Generic;
using System.Linq;

namespace NuvioTV.Core.Debrid
{
    /// <summary>
    /// Normalized remote debrid file entry. Covers the provider-specific field
    /// spellings consumed by js/core/debrid/debridFileSelection.js:
    /// TorBox {id,name,size}, Premiumize {link,size,mime}, Real-Debrid
    /// {id,path,bytes}.
    /// </summary>
    public sealed class DebridRemoteFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ShortName { get; set; }
        public string Path { get; set; }
        public string AbsolutePath { get; set; }
        public string MimeType { get; set; }
        public double? Size { get; set; }
        public double? Bytes { get; set; }
        public string Link { get; set; }
    }

    /// <summary>
    /// Debrid file picking logic. Verbatim port of
    /// js/core/debrid/debridFileSelection.js.
    /// </summary>
    public static class DebridFileSelection
    {
        private static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".wmv", ".flv"
        };

        private static readonly char[] PathSeparators = { '/', '\\' };

        /// <summary>js hasDebridVideoExtension.</summary>
        public static bool HasVideoExtension(string value)
        {
            var name = (value ?? "").Trim().ToLowerInvariant();
            foreach (var extension in VideoExtensions)
            {
                if (name.EndsWith(extension, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>js displayName: last path segment of the first non-empty name-ish field.</summary>
        public static string GetDisplayName(DebridRemoteFile file)
        {
            if (file == null)
            {
                return "";
            }
            var candidate = FirstNonEmpty(file.Name, file.ShortName, file.Path, file.AbsolutePath);
            if (candidate == null)
            {
                return "";
            }
            var segments = candidate
                .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            return segments.Count > 0 ? segments[segments.Count - 1] : "";
        }

        /// <summary>js normalizedFileName: last segment, extension stripped, lowercased, non-alnum → space.</summary>
        public static string NormalizeFileName(string value)
        {
            var segments = (value ?? "")
                .Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            var last = segments.Count > 0 ? segments[segments.Count - 1] : null;
            if (last == null)
            {
                return "";
            }
            var dot = last.LastIndexOf('.');
            if (dot >= 0)
            {
                last = last.Substring(0, dot);
            }
            var builder = new System.Text.StringBuilder();
            foreach (var ch in last.ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    builder.Append(ch);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            return builder.ToString().Trim();
        }

        /// <summary>js fileSize: size ?? bytes ?? 0.</summary>
        public static double GetFileSize(DebridRemoteFile file)
        {
            if (file == null)
            {
                return 0;
            }
            return file.Size ?? file.Bytes ?? 0d;
        }

        /// <summary>
        /// Pick the playable file for playback. Verbatim port of selectDebridFile:
        /// name match against resolve identity names → episode pattern match →
        /// fileIdx (zero-based, then one-based, then id match) → largest playable.
        /// </summary>
        public static DebridRemoteFile SelectFile(
            IReadOnlyList<DebridRemoteFile> files,
            DebridClientResolve resolve,
            int? season,
            int? episode,
            string kind)
        {
            var list = files ?? (IReadOnlyList<DebridRemoteFile>)Array.Empty<DebridRemoteFile>();
            var playable = list.Where(file => IsPlayableVideo(file, kind)).ToList();
            if (playable.Count == 0)
            {
                return null;
            }

            resolve = resolve ?? new DebridClientResolve();
            var episodePatterns = BuildEpisodePatterns(
                season ?? resolve.Season,
                episode ?? resolve.Episode);
            var names = SpecificFileNames(resolve, episodePatterns);
            if (names.Count > 0)
            {
                var matched = playable.FirstOrDefault(file =>
                {
                    var normalized = NormalizeFileName(GetDisplayName(file));
                    return names.Any(name => normalized.Contains(name) || name.Contains(normalized));
                });
                if (matched != null)
                {
                    return matched;
                }
            }

            if (episodePatterns.Count > 0)
            {
                var matched = playable.FirstOrDefault(file =>
                {
                    var name = (GetDisplayName(file) ?? "").ToLowerInvariant();
                    return episodePatterns.Any(pattern => name.Contains(pattern));
                });
                if (matched != null)
                {
                    return matched;
                }
            }

            if (resolve.FileIdx != null)
            {
                var fileIdx = resolve.FileIdx.Value;
                if (fileIdx >= 0 && fileIdx < list.Count)
                {
                    var byIndex = list[fileIdx];
                    if (IsPlayableVideo(byIndex, kind))
                    {
                        return byIndex;
                    }
                }
                if (fileIdx > 0 && fileIdx - 1 < list.Count)
                {
                    var byOneBasedIndex = list[fileIdx - 1];
                    if (IsPlayableVideo(byOneBasedIndex, kind))
                    {
                        return byOneBasedIndex;
                    }
                }
                var byId = playable.FirstOrDefault(file =>
                    string.Equals(file.Id, fileIdx.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        StringComparison.Ordinal));
                if (byId != null)
                {
                    return byId;
                }
            }

            DebridRemoteFile best = playable[0];
            foreach (var file in playable)
            {
                if (GetFileSize(file) > GetFileSize(best))
                {
                    best = file;
                }
            }
            return best;
        }

        /// <summary>js buildEpisodePatterns: sXXeYY, NxNN, NxN.</summary>
        public static IReadOnlyList<string> BuildEpisodePatterns(int? seasonOrNull, int? episodeOrNull)
        {
            var seasonNumber = seasonOrNull ?? 0;
            var episodeNumber = episodeOrNull ?? 0;
            if (seasonNumber <= 0 || episodeNumber <= 0)
            {
                return Array.Empty<string>();
            }
            var seasonTwo = seasonNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            var episodeTwo = episodeNumber.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            return new[]
            {
                "s" + seasonTwo + "e" + episodeTwo,
                seasonNumber + "x" + episodeTwo,
                seasonNumber + "x" + episodeNumber
            };
        }

        private static bool LooksSpecific(string value, IReadOnlyList<string> episodePatterns)
        {
            var lower = (value ?? "").ToLowerInvariant();
            return HasVideoExtension(lower) || episodePatterns.Any(lower.Contains);
        }

        private static IReadOnlyList<string> SpecificFileNames(
            DebridClientResolve resolve,
            IReadOnlyList<string> episodePatterns)
        {
            var raw = resolve.Stream != null ? resolve.Stream.Raw : null;
            var parsed = raw != null ? raw.Parsed : null;
            var rawTitle = parsed != null ? parsed.RawTitle : null;
            var torrentName = resolve.TorrentName;

            var candidates = new[]
            {
                resolve.Filename,
                raw?.Filename,
                LooksSpecific(rawTitle, episodePatterns) ? rawTitle : null,
                LooksSpecific(torrentName, episodePatterns) ? torrentName : null
            };

            var seen = new List<string>();
            foreach (var candidate in candidates)
            {
                var normalized = NormalizeFileName(candidate);
                if (normalized.Length > 0 && !seen.Contains(normalized))
                {
                    seen.Add(normalized);
                }
            }
            return seen;
        }

        private static bool IsPlayableVideo(DebridRemoteFile file, string kind)
        {
            if (file == null)
            {
                return false;
            }
            var mime = (file.MimeType ?? "").ToLowerInvariant();
            if (mime.StartsWith("video/", StringComparison.Ordinal))
            {
                return true;
            }
            if (kind == "premiumize" && !string.IsNullOrEmpty(file.Link) && GetDisplayName(file).Length == 0)
            {
                return true;
            }
            return HasVideoExtension(GetDisplayName(file));
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }
    }
}
