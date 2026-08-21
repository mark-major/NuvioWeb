using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents metadata for a piece of content (movie, series, etc.).
    /// Source: js/domain/model/meta.js createMeta
    /// </summary>
    public sealed class Meta
    {
        [JsonPropertyName("id")]
        public string Id { get; }

        [JsonPropertyName("type")]
        public string Type { get; }

        [JsonPropertyName("name")]
        public string Name { get; }

        [JsonPropertyName("poster")]
        public string Poster { get; }

        [JsonPropertyName("background")]
        public string Background { get; }

        [JsonPropertyName("logo")]
        public string Logo { get; }

        [JsonPropertyName("description")]
        public string Description { get; }

        [JsonPropertyName("genres")]
        public IReadOnlyList<string> Genres { get; }

        [JsonPropertyName("videos")]
        public IReadOnlyList<MetaVideo> Videos { get; }

        [JsonPropertyName("releaseInfo")]
        public string ReleaseInfo { get; }

        public Meta(
            string id,
            string type,
            string name,
            string poster,
            string background,
            string logo,
            string description,
            IReadOnlyList<string> genres,
            IReadOnlyList<MetaVideo> videos,
            string releaseInfo
        )
        {
            Id = id;
            Type = type;
            Name = name;
            Poster = poster;
            Background = background;
            Logo = logo;
            Description = description;
            Genres = genres;
            Videos = videos;
            ReleaseInfo = releaseInfo;
        }
    }
}
