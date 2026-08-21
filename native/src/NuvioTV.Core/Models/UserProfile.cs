using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a user profile with settings and preferences.
    /// Source: js/core/profile/profileManager.js normalizeProfile
    /// </summary>
    public sealed class UserProfile
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("profileIndex")]
        public int ProfileIndex { get; }

        [JsonPropertyName("name")]
        public string Name { get; }

        [JsonPropertyName("avatarColorHex")]
        public string AvatarColorHex { get; }

        [JsonPropertyName("isPrimary")]
        public bool IsPrimary { get; }

        [JsonPropertyName("usesPrimaryAddons")]
        public bool UsesPrimaryAddons { get; }

        [JsonPropertyName("usesPrimaryPlugins")]
        public bool UsesPrimaryPlugins { get; }

        [JsonPropertyName("avatarId")]
        public string AvatarId { get; }

        [JsonPropertyName("avatarUrl")]
        public string AvatarUrl { get; }

        public UserProfile(
            string id,
            int profileIndex,
            string name,
            string avatarColorHex,
            bool isPrimary,
            bool usesPrimaryAddons,
            bool usesPrimaryPlugins,
            string avatarId,
            string avatarUrl
        )
        {
            Id = id;
            ProfileIndex = profileIndex;
            Name = name;
            AvatarColorHex = avatarColorHex;
            IsPrimary = isPrimary;
            UsesPrimaryAddons = usesPrimaryAddons;
            UsesPrimaryPlugins = usesPrimaryPlugins;
            AvatarId = avatarId;
            AvatarUrl = avatarUrl;
        }
    }
}
