using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents an addon with its configuration and catalogs.
    /// Source: js/domain/model/addon.js createAddon
    /// </summary>
    public sealed class Addon
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("name")]
        public string Name { get; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; }

        [JsonPropertyName("version")]
        public string Version { get; }

        [JsonPropertyName("description")]
        public string Description { get; }

        [JsonPropertyName("logo")]
        public string Logo { get; }

        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; }

        [JsonPropertyName("catalogs")]
        public IReadOnlyList<AddonCatalog> Catalogs { get; }

        [JsonPropertyName("types")]
        public IReadOnlyList<string> Types { get; }

        [JsonPropertyName("rawTypes")]
        public IReadOnlyList<string> RawTypes { get; }

        [JsonPropertyName("idPrefixes")]
        public IReadOnlyList<string> IdPrefixes { get; }

        [JsonPropertyName("resources")]
        public IReadOnlyList<AddonResource> Resources { get; }

        public Addon(
            string id,
            string name,
            string displayName,
            string version,
            string description,
            string logo,
            string baseUrl,
            IReadOnlyList<AddonCatalog> catalogs,
            IReadOnlyList<string> types,
            IReadOnlyList<string> rawTypes,
            IReadOnlyList<string> idPrefixes,
            IReadOnlyList<AddonResource> resources
        )
        {
            Id = id;
            Name = name;
            DisplayName = displayName;
            Version = version;
            Description = description;
            Logo = logo;
            BaseUrl = baseUrl;
            Catalogs = catalogs;
            Types = types;
            RawTypes = rawTypes;
            IdPrefixes = idPrefixes ?? new List<string>();
            Resources = resources;
        }
    }
}
