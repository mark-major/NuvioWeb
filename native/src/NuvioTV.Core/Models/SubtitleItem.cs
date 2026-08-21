using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents a subtitle track for a stream.
    /// Source: js/domain/model/subtitle.js createSubtitle
    /// </summary>
    public sealed class SubtitleItem
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("url")]
        public string Url { get; }

        [JsonPropertyName("lang")]
        public string Lang { get; }

        [JsonPropertyName("addonName")]
        public string AddonName { get; }

        [JsonPropertyName("addonLogo")]
        public string AddonLogo { get; }

        public SubtitleItem(
            string id,
            string url,
            string lang,
            string addonName = null,
            string addonLogo = null
        )
        {
            Id = id;
            Url = url;
            Lang = lang;
            AddonName = addonName;
            AddonLogo = addonLogo;
        }
    }
}
