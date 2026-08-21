using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents watch progress for a piece of content.
    /// Source: js/domain/model/watchProgress.js createWatchProgress
    /// </summary>
    public sealed class WatchProgress
    {
        [JsonPropertyName("contentId")]
        public string ContentId { get; }

        [JsonPropertyName("contentType")]
        public string ContentType { get; }

        [JsonPropertyName("videoId")]
        public string VideoId { get; }

        [JsonPropertyName("positionMs")]
        public long PositionMs { get; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; }

        [JsonPropertyName("updatedAt")]
        public long UpdatedAt { get; }

        /// <summary>
        /// Threshold for considering content as started (2%).
        /// Source: js/domain/model/watchProgress.js WATCH_PROGRESS_STARTED_THRESHOLD
        /// </summary>
        public const double StartedThreshold = 0.02;

        /// <summary>
        /// Threshold for considering content as completed (90%).
        /// Source: js/domain/model/watchProgress.js WATCH_PROGRESS_COMPLETED_THRESHOLD
        /// </summary>
        public const double CompletedThreshold = 0.90;

        public WatchProgress(
            string contentId,
            string contentType,
            string videoId,
            long positionMs,
            long durationMs,
            long updatedAt
        )
        {
            ContentId = contentId;
            ContentType = contentType;
            VideoId = videoId;
            PositionMs = positionMs;
            DurationMs = durationMs;
            UpdatedAt = updatedAt;
        }
    }
}
