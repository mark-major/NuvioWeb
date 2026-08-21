using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a resource within an addon.
    /// Source: js/domain/model/addon.js resources property
    /// </summary>
    public sealed class AddonResource
    {
        [JsonPropertyName("name")]
        public string Name { get; }

        [JsonPropertyName("type")]
        public string Type { get; }

        [JsonPropertyName("url")]
        public string Url { get; }

        public AddonResource(string name, string type, string url)
        {
            Name = name;
            Type = type;
            Url = url;
        }
    }
}
