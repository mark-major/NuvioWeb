using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a playable stream with metadata and sources.
    /// Source: js/domain/model/stream.js createStream
    /// </summary>
    public sealed class StreamItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("ytId")]
        public string YtId { get; set; }

        [JsonPropertyName("infoHash")]
        public string InfoHash { get; set; }

        [JsonPropertyName("fileIdx")]
        public int? FileIdx { get; set; }

        [JsonPropertyName("externalUrl")]
        public string ExternalUrl { get; set; }

        [JsonPropertyName("behaviorHints")]
        public StreamBehaviorHints BehaviorHints { get; set; }

        [JsonPropertyName("addonName")]
        public string AddonName { get; set; }

        [JsonPropertyName("addonLogo")]
        public string AddonLogo { get; set; }

        [JsonPropertyName("subtitles")]
        public IReadOnlyList<SubtitleItem> Subtitles { get; set; }

        [JsonPropertyName("sources")]
        public IReadOnlyList<object> Sources { get; set; }

        [JsonPropertyName("quality")]
        public string Quality { get; set; }

        [JsonPropertyName("qualityValue")]
        public int QualityValue { get; set; }

        [JsonPropertyName("clientResolve")]
        public ClientResolve ClientResolve { get; set; }

        [JsonPropertyName("debridCacheStatus")]
        public DebridCacheStatus DebridCacheStatus { get; set; }

        public StreamItem()
        {
            Subtitles = new List<SubtitleItem>();
            Sources = new List<object>();
        }
    }
}
