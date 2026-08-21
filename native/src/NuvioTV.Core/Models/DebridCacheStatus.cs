using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Debrid cache status for torrents.
    /// Indicates whether content is cached on debrid service.
    /// Source: js/core/debrid/localDebridAvailabilityService.js
    /// </summary>
    public sealed class DebridCacheStatus
    {
        [JsonPropertyName("providerId")]
        public string ProviderId { get; set; }

        [JsonPropertyName("providerName")]
        public string ProviderName { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("cachedName")]
        public string CachedName { get; set; }

        [JsonPropertyName("cachedSize")]
        public long CachedSize { get; set; }

        // State constants
        public const string StateCached = "CACHED";
        public const string StateNotCached = "NOT_CACHED";
        public const string StateChecking = "CHECKING";
        public const string StateUnknown = "UNKNOWN";
    }
}
