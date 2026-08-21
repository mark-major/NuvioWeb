using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Envelope for profile-scoped data storage.
    /// The __profileScoped marker is required for profileScopedStore.js to identify profile-scoped values.
    /// Source: js/data/local/profileScopedStore.js (isProfileScopedValue requires __profileScoped===true)
    /// </summary>
    public sealed class ProfileEnvelope
    {
        [JsonPropertyName("__profileScoped")]
        public bool ProfileScoped { get; } = true;

        [JsonPropertyName("version")]
        public int Version { get; }

        [JsonPropertyName("profiles")]
        public Dictionary<string, object> Profiles { get; }

        public ProfileEnvelope(int version, Dictionary<string, object> profiles)
        {
            Version = version;
            Profiles = profiles;
        }
    }
}
