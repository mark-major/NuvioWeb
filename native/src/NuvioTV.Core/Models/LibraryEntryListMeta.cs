using System.Text.Json.Serialization;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// List-specific metadata for library entries.
    /// Source: js/data/repository/libraryRepository.js LibraryEntryListMeta typedef
    /// </summary>
    public sealed class LibraryEntryListMeta
    {
        [JsonPropertyName("listedAt")]
        public long ListedAt { get; }

        [JsonPropertyName("traktRank")]
        public long TrankRank { get; }

        public LibraryEntryListMeta(long listedAt, long trankRank = 0)
        {
            ListedAt = listedAt;
            TrankRank = trankRank;
        }
    }
}
