using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a video within a meta item.
    /// Source: js/domain/model/meta.js videos property
    /// </summary>
    public sealed class MetaVideo
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("codec")]
        public string Codec { get; }

        [JsonPropertyName("height")]
        public int Height { get; }

        public MetaVideo(string id, string codec, int height)
        {
            Id = id;
            Codec = codec;
            Height = height;
        }
    }
}
