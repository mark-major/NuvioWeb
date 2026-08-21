using System;
using System.Collections.Generic;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// Port of audioTrackCodecMetadata.js: maps raw codec identifiers to the
    /// display labels used by the audio track list (TrueHD, DTS-HD MA,
    /// E-AC-3 JOC, etc.). Pure string logic; host-testable.
    /// </summary>
    public static class AudioTrackCodecMetadata
    {
        public const string Unknown = "Unknown";

        private static readonly Dictionary<string, string> CodecLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["aac"] = "AAC",
                ["mp3"] = "MP3",
                ["ac3"] = "AC-3",
                ["eac3"] = "E-AC-3",
                ["ec3"] = "E-AC-3",
                ["joc"] = "E-AC-3 JOC",
                ["ddplus"] = "DD+",
                ["truehd"] = "TrueHD",
                ["dts"] = "DTS",
                ["dtshd"] = "DTS-HD",
                ["dtshdma"] = "DTS-HD MA",
                ["dtshra"] = "DTS-HRA",
                ["dtsx"] = "DTS:X",
                ["opus"] = "Opus",
                ["vorbis"] = "Vorbis",
                ["flac"] = "FLAC",
                ["pcm"] = "PCM",
                ["lpcm"] = "LPCM",
                ["alac"] = "ALAC",
                ["wma"] = "WMA"
            };

        /// <summary>Maps a raw codec identifier to its display label.</summary>
        public static string LabelFor(string codecType)
        {
            if (string.IsNullOrWhiteSpace(codecType)) return Unknown;
            var normalized = NormalizeKey(codecType);
            foreach (var key in CodecLabels.Keys)
            {
                if (normalized.Contains(NormalizeKey(key)))
                {
                    return CodecLabels[key];
                }
            }
            return codecType.Trim().ToUpperInvariant();
        }

        private static string NormalizeKey(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }
            return sb.ToString();
        }
    }
}
