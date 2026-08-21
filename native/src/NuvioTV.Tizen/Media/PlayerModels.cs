using System;
using System.Collections.Generic;

namespace NuvioTV.Tizen.Media
{
    /// <summary>External subtitle track supplied with playback.</summary>
    public sealed class ExternalSubtitle
    {
        public Uri Uri { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
    }

    public sealed class PlayerSource
    {
        public Uri Url { get; set; }
        public string UserAgent { get; set; }
        public IReadOnlyDictionary<string, string> Headers { get; set; }
        public string Cookie { get; set; }
        public IReadOnlyList<ExternalSubtitle> ExternalSubtitles { get; set; }
            = new List<ExternalSubtitle>();
    }

    public sealed class PlayerOptions
    {
        /// <summary>Aspect mode: "original" | "16x9" | "4x3" | "full" (playerScreen parity).</summary>
        public string AspectMode { get; set; } = "original";
        public int BufferingTimeMs { get; set; } = 4096;
    }

    public sealed class PlayerPrepareResult
    {
        public long DurationMs;
        public IReadOnlyList<PlayerAudioTrack> AudioTracks = Array.Empty<PlayerAudioTrack>();
        public IReadOnlyList<PlayerSubtitleTrack> SubtitleTracks = Array.Empty<PlayerSubtitleTrack>();
    }

    public sealed class PlayerAudioTrack
    {
        public int Index;
        public string Language;
        public string CodecLabel;
    }

    public sealed class PlayerSubtitleTrack
    {
        public int Index;
        public string Language;
    }

    public enum PlayerEventType
    {
        Buffering,
        Completed,
        Interrupted,
        Error,
        VideoStreamChanged
    }

    public sealed class PlayerEvent : EventArgs
    {
        public PlayerEventType Type { get; set; }
        /// <summary>Buffering percentage for Buffering; error message for Error.</summary>
        public string Detail { get; set; }
        public int ProgressPercent { get; set; }
    }
}
