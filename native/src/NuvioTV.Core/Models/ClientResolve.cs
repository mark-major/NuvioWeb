using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Client-side resolution configuration for streams.
    /// Used by direct debrid resolver and Tizen streaming server.
    /// Source: js/core/debrid/directDebridResolver.js, js/core/p2p/tizenStreamingServerResolver.js
    /// </summary>
    public sealed class ClientResolve
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("service")]
        public string Service { get; set; }

        [JsonPropertyName("infoHash")]
        public string InfoHash { get; set; }

        [JsonPropertyName("fileIdx")]
        public int? FileIdx { get; set; }

        [JsonPropertyName("filename")]
        public string Filename { get; set; }

        [JsonPropertyName("torrentName")]
        public string TorrentName { get; set; }

        [JsonPropertyName("magnetUri")]
        public string MagnetUri { get; set; }

        [JsonPropertyName("sources")]
        public IReadOnlyList<string> Sources { get; set; }

        public ClientResolve()
        {
            Sources = new List<string>();
        }
    }
}
