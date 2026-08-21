using System;

namespace NuvioTV.Core.Models
{
    /// <summary>
    /// Content type constants matching JS ContentType values.
    /// Source: js/domain/model/contentType.js
    /// </summary>
    public static class ContentTypes
    {
        public const string Movie = "movie";
        public const string Series = "series";
        public const string Tv = "tv";
        public const string Channel = "channel";
        public const string Anime = "anime";

        /// <summary>
        /// Normalizes a content type string to a valid type.
        /// Matches JS ContentType.fromString behavior.
        /// </summary>
        public static string FromString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Movie;
            }

            var normalized = value.Trim().ToLowerInvariant();
            return normalized;
        }
    }
}
