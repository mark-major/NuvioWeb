using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Profile-scoped storage envelope containing profile-specific data.
    /// Source: js/data/local/profileScopedStore.js createEmptyEnvelope
    /// </summary>
    public sealed class ProfileEnvelope
    {
        [JsonPropertyName("version")]
        public int Version { get; }

        [JsonPropertyName("profiles")]
        public IReadOnlyDictionary<string, object> Profiles { get; }

        public ProfileEnvelope(int version, IReadOnlyDictionary<string, object> profiles)
        {
            Version = version;
            Profiles = profiles;
        }
    }
}
