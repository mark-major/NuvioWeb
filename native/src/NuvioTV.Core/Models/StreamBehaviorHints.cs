using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Behavior hints for stream items.
    /// Source: js/domain/model/stream.js behaviorHints property
    /// </summary>
    public sealed class StreamBehaviorHints
    {
        [JsonPropertyName("isProxy")]
        public bool IsProxy { get; set; }

        [JsonPropertyName("notWebReady")]
        public bool NotWebReady { get; set; }

        public StreamBehaviorHints()
        {
            IsProxy = false;
            NotWebReady = false;
        }
    }
}
