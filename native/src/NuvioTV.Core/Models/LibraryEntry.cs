using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Represents an entry in the user's library.
    /// Source: js/data/repository/libraryRepository.js LibraryEntry typedef
    /// </summary>
    public sealed class LibraryEntry
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

        [JsonPropertyName("description")]
        public string Description { get; }

        [JsonPropertyName("releaseInfo")]
        public string ReleaseInfo { get; }

        [JsonPropertyName("imdbRating")]
        public double ImdbRating { get; }

        [JsonPropertyName("genres")]
        public IReadOnlyList<string> Genres { get; }

        [JsonPropertyName("addonBaseUrl")]
        public string AddonBaseUrl { get; }

        [JsonPropertyName("listKeys")]
        public IReadOnlyList<string> ListKeys { get; }

        [JsonPropertyName("listedAt")]
        public long ListedAt { get; }

        [JsonPropertyName("traktRank")]
        public long TraktRank { get; }

        [JsonPropertyName("listMeta")]
        public IReadOnlyDictionary<string, LibraryEntryListMeta> ListMeta { get; }

        public LibraryEntry(
            string id,
            string type,
            string name,
            string poster,
            string background,
            string description,
            string releaseInfo,
            double imdbRating,
            IReadOnlyList<string> genres,
            string addonBaseUrl,
            IReadOnlyList<string> listKeys,
            long listedAt,
            long traktRank,
            IReadOnlyDictionary<string, LibraryEntryListMeta> listMeta
        )
        {
            Id = id;
            Type = type;
            Name = name;
            Poster = poster;
            Background = background;
            Description = description;
            ReleaseInfo = releaseInfo;
            ImdbRating = imdbRating;
            Genres = genres;
            AddonBaseUrl = addonBaseUrl;
            ListKeys = listKeys;
            ListedAt = listedAt;
            TraktRank = traktRank;
            ListMeta = listMeta;
        }
    }
}
