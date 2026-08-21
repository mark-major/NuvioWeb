using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a catalog within an addon.
    /// Source: js/data/repository/addonRepository.js (catalog entries inside addon objects)
    /// </summary>
    public sealed class AddonCatalog
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("apiType")]
        public string ApiType { get; set; }

        [JsonPropertyName("extra")]
        public IReadOnlyList<AddonCatalogExtra> Extra { get; set; }

        public AddonCatalog()
        {
            Extra = new List<AddonCatalogExtra>();
        }
    }

    /// <summary>
    /// Extra parameter for catalog requests.
    /// Per manifest spec.
    /// </summary>
    public sealed class AddonCatalogExtra
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("options")]
        public IReadOnlyList<string> Options { get; set; }

        public AddonCatalogExtra()
        {
            Options = new List<string>();
        }
    }
}
