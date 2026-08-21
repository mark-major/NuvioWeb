using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a resource provided by an addon.
    /// Source: js/data/repository/addonRepository.js (canonical shape)
    /// </summary>
    public sealed class AddonResource
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("types")]
        public IReadOnlyList<string> Types { get; set; }

        [JsonPropertyName("idPrefixes")]
        public IReadOnlyList<string> IdPrefixes { get; set; }

        public AddonResource()
        {
            Types = new List<string>();
            IdPrefixes = new List<string>();
        }
    }
}
