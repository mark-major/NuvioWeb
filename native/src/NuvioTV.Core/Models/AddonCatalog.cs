using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a catalog within an addon.
    /// Source: js/domain/model/addon.js catalogs property
    /// </summary>
    public sealed class AddonCatalog
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("type")]
        public string Type { get; }

        [JsonPropertyName("name")]
        public string Name { get; }

        public AddonCatalog(string id, string type, string name)
        {
            Id = id;
            Type = type;
            Name = name;
        }
    }
}
