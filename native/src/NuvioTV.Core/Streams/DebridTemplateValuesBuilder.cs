using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NuvioTV.Core.Debrid;

namespace NuvioTV.Core.Streams
{
    /// <summary>
    /// Builds the flat dotted-key value dictionary consumed by
    /// <see cref="DebridStreamTemplateEngine.Render"/>. Verbatim port of
    /// js/core/debrid/directDebridStreamPresentation.js buildTemplateValues
    /// (~868-941) plus its private helpers (labelUnlessUnknown,
    /// labelsExcludingUnknown, buildSeasonEpisodeList, formatEpisodes,
    /// formatSeasons, languageEmoji, streamType, serviceCached).
    /// Numbers are boxed as long/double because the template engine coerces
    /// only those numerically.
    /// </summary>
    public static class DebridTemplateValuesBuilder
    {
        private static readonly Dictionary<string, string> ResolutionLabels =
            new Dictionary<string, string>
            {
                { "P2160", "2160p" },
                { "P1440", "1440p" },
                { "P1080", "1080p" },
                { "P720", "720p" },
                { "P576", "576p" },
                { "P480", "480p" },
                { "P360", "360p" }
            };

        private static readonly Dictionary<string, string> QualityLabels =
            new Dictionary<string, string>
            {
                { "BLURAY_REMUX", "BluRay REMUX" },
                { "BLURAY", "BluRay" },
                { "WEB_DL", "WEB-DL" },
                { "WEBRIP", "WEBRip" },
                { "HDRIP", "HDRip" },
                { "HD_RIP", "HC HD-Rip" },
                { "DVDRIP", "DVDRip" },
                { "HDTV", "HDTV" },
                { "CAM", "CAM" },
                { "TS", "TS" },
                { "TC", "TC" },
                { "SCR", "SCR" }
            };

        private static readonly Dictionary<string, string> EncodeLabels =
            new Dictionary<string, string>
            {
                { "AV1", "AV1" },
                { "HEVC", "HEVC" },
                { "AVC", "AVC" },
                { "XVID", "XviD" },
                { "DIVX", "DivX" }
            };

        private static readonly Dictionary<string, string> VisualTagLabels =
            new Dictionary<string, string>
            {
                { "HDR_DV", "HDR+DV" },
                { "DV_ONLY", "DV Only" },
                { "HDR_ONLY", "HDR Only" },
                { "HDR10_PLUS", "HDR10+" },
                { "HDR10", "HDR10" },
                { "DV", "DV" },
                { "HDR", "HDR" },
                { "HLG", "HLG" },
                { "TEN_BIT", "10bit" },
                { "THREE_D", "3D" },
                { "IMAX", "IMAX" },
                { "AI", "AI" },
                { "SDR", "SDR" },
                { "H_OU", "H-OU" },
                { "H_SBS", "H-SBS" }
            };

        private static readonly Dictionary<string, string> AudioTagLabels =
            new Dictionary<string, string>
            {
                { "ATMOS", "Atmos" },
                { "DD_PLUS", "DD+" },
                { "DD", "DD" },
                { "DTS_X", "DTS:X" },
                { "DTS_HD_MA", "DTS-HD MA" },
                { "DTS_HD", "DTS-HD" },
                { "DTS_ES", "DTS-ES" },
                { "DTS", "DTS" },
                { "TRUEHD", "TrueHD" },
                { "OPUS", "OPUS" },
                { "FLAC", "FLAC" },
                { "AAC", "AAC" }
            };

        private static readonly Dictionary<string, string> AudioChannelLabels =
            new Dictionary<string, string>
            {
                { "CH_2_0", "2.0" },
                { "CH_5_1", "5.1" },
                { "CH_6_1", "6.1" },
                { "CH_7_1", "7.1" }
            };

        // js LANGUAGE_LABELS[id][0] — the short code used for stream.languages
        // and the input to languageEmoji.
        private static readonly Dictionary<string, string> LanguageCodes =
            new Dictionary<string, string>
            {
                { "EN", "en" },
                { "HI", "hi" },
                { "IT", "it" },
                { "ES", "es" },
                { "FR", "fr" },
                { "DE", "de" },
                { "PT_BR", "pt-br" },
                { "PT", "pt" },
                { "PL", "pl" },
                { "CS", "cs" },
                { "LA", "la" },
                { "JA", "ja" },
                { "KO", "ko" },
                { "ZH", "zh" },
                { "MULTI", "multi" }
            };

        /// <summary>js buildTemplateValues(stream, fact).</summary>
        public static IDictionary<string, object> Build(DebridStream stream, DebridStreamFact fact)
        {
            stream = stream ?? new DebridStream();
            fact = fact ?? new DebridStreamFact();
            var resolve = stream.ClientResolve ?? new DebridClientResolve();
            var raw = resolve.Stream != null ? resolve.Stream.Raw : null;
            var parsed = raw != null && raw.Parsed != null ? raw.Parsed : new DebridParsedMetadata();

            var seasons = ToLongList(parsed.Seasons);
            var episodes = ToLongList(parsed.Episodes);
            var visualTags = ToLabelList(parsed.Hdr);
            if (!string.IsNullOrEmpty(parsed.BitDepth))
            {
                visualTags.Add(parsed.BitDepth);
            }
            var audioTags = ToLabelList(parsed.Audio);
            var audioChannels = ToLabelList(parsed.Channels);
            var languages = ToLabelList(parsed.Languages);

            var providerId = FirstNonEmpty(
                stream.DebridCacheStatus != null ? stream.DebridCacheStatus.ProviderId : null,
                resolve.Service);
            var provider = DebridProviders.ById(providerId);
            var serviceShortName = FirstNonEmpty(
                (resolve.ServiceExtension ?? "").Trim(),
                provider != null ? provider.ShortName : null) ?? "";

            var factLanguages = FactLanguageLabels(fact.Languages);

            return new Dictionary<string, object>
            {
                ["stream.title"] = FirstNonEmpty(parsed.ParsedTitle, resolve.Title, stream.Title),
                ["stream.year"] = BoxNumber(parsed.Year),
                ["stream.season"] = BoxNumber(resolve.Season),
                ["stream.episode"] = BoxNumber(resolve.Episode),
                ["stream.seasons"] = seasons,
                ["stream.episodes"] = episodes,
                ["stream.seasonEpisode"] = BuildSeasonEpisodeList(
                    resolve.Season, resolve.Episode, seasons, episodes),
                ["stream.formattedEpisodes"] = FormatEpisodes(episodes),
                ["stream.formattedSeasons"] = FormatSeasons(seasons),
                ["stream.resolution"] = FirstNonEmpty(
                    parsed.Resolution, LabelUnlessUnknown(fact.Resolution, ResolutionLabels)),
                ["stream.library"] = false,
                ["stream.quality"] = FirstNonEmpty(
                    parsed.Quality, LabelUnlessUnknown(fact.Quality, QualityLabels)),
                ["stream.visualTags"] = visualTags.Count > 0
                    ? visualTags
                    : LabelsExcludingUnknown(fact.VisualTags, VisualTagLabels),
                ["stream.audioTags"] = audioTags.Count > 0
                    ? audioTags
                    : LabelsExcludingUnknown(fact.AudioTags, AudioTagLabels),
                ["stream.audioChannels"] = audioChannels.Count > 0
                    ? audioChannels
                    : LabelsExcludingUnknown(fact.AudioChannels, AudioChannelLabels),
                ["stream.languages"] = languages.Count > 0 ? languages : factLanguages,
                ["stream.languageEmojis"] = (languages.Count > 0 ? languages : factLanguages)
                    .Select(LanguageEmoji)
                    .ToList(),
                ["stream.size"] = BoxSize(raw != null ? raw.Size : null)
                    ?? BoxNumber(stream.VideoSizeHint)
                    ?? (stream.DebridCacheStatus != null
                        ? (object)(long)stream.DebridCacheStatus.CachedSize
                        : null),
                ["stream.folderSize"] = BoxSize(raw != null ? raw.FolderSize : null),
                ["stream.encode"] = FirstNonEmpty(
                    parsed.Codec != null ? parsed.Codec.ToUpperInvariant() : null,
                    LabelUnlessUnknown(fact.Codec, EncodeLabels)),
                ["stream.indexer"] = FirstNonEmpty(raw != null ? raw.Indexer : null, raw != null ? raw.Tracker : null),
                ["stream.network"] = FirstNonEmpty(parsed.Network, raw != null ? raw.Network : null),
                ["stream.releaseGroup"] = FirstNonEmpty(parsed.Group, fact.ReleaseGroup),
                ["stream.duration"] = parsed.Duration.HasValue ? (object)parsed.Duration.Value : null,
                ["stream.edition"] = FirstNonEmpty(parsed.Edition, fact.Edition),
                ["stream.filename"] = FirstNonEmpty(
                    raw != null ? raw.Filename : null,
                    resolve.Filename,
                    stream.FilenameHint,
                    stream.DebridCacheStatus != null ? stream.DebridCacheStatus.CachedName : null),
                ["stream.regexMatched"] = null,
                ["stream.type"] = StreamType(stream, resolve),
                ["service.cached"] = ServiceCached(stream, resolve),
                ["service.shortName"] = serviceShortName,
                ["service.name"] = provider != null
                    ? provider.DisplayName
                    : DebridProviders.DisplayName(providerId),
                ["addon.name"] = FirstNonEmpty(stream.AddonName, stream.RawAddonName)
            };
        }

        // ------------------------------------------------------------------
        // js helpers
        // ------------------------------------------------------------------

        /// <summary>js labelUnlessUnknown.</summary>
        private static string LabelUnlessUnknown(string value, Dictionary<string, string> labels)
        {
            if (string.IsNullOrEmpty(value) || value == "UNKNOWN")
            {
                return null;
            }
            return labels.TryGetValue(value, out var label) ? label : value;
        }

        /// <summary>js labelsExcludingUnknown.</summary>
        private static List<object> LabelsExcludingUnknown(
            IEnumerable<string> values, Dictionary<string, string> labels)
        {
            var result = new List<object>();
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                if (value == "UNKNOWN")
                {
                    continue;
                }
                var label = labels.TryGetValue(value, out var mapped) ? mapped : value;
                if (!string.IsNullOrEmpty(label))
                {
                    result.Add(label);
                }
            }
            return result;
        }

        /// <summary>js toArray over numeric lists: drop null/empty entries.</summary>
        private static List<object> ToLongList(IEnumerable<int> values)
        {
            var result = new List<object>();
            foreach (var value in values ?? Enumerable.Empty<int>())
            {
                result.Add((long)value);
            }
            return result;
        }

        private static List<object> ToLabelList(IEnumerable<string> values)
        {
            var result = new List<object>();
            foreach (var value in values ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        /// <summary>js (fact.languages || []).map(LANGUAGE_LABELS[language]?.[0]).filter(Boolean).</summary>
        private static List<object> FactLanguageLabels(IEnumerable<string> factLanguages)
        {
            var result = new List<object>();
            foreach (var id in factLanguages ?? Enumerable.Empty<string>())
            {
                if (LanguageCodes.TryGetValue(id, out var code))
                {
                    result.Add(code);
                }
            }
            return result;
        }

        private static string TwoDigits(long value)
        {
            return Math.Truncate((double)value).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>js buildSeasonEpisodeList.</summary>
        private static List<object> BuildSeasonEpisodeList(
            int? season, int? episode, List<object> seasons, List<object> episodes)
        {
            if (season.HasValue && episode.HasValue)
            {
                return new List<object>
                {
                    "S" + TwoDigits(season.Value) + "E" + TwoDigits(episode.Value)
                };
            }
            if (seasons.Count == 0 || episodes.Count == 0)
            {
                return new List<object>();
            }
            var list = new List<object>();
            foreach (var seasonEntry in seasons)
            {
                foreach (var episodeEntry in episodes)
                {
                    list.Add("S" + TwoDigits((long)seasonEntry) + "E" + TwoDigits((long)episodeEntry));
                }
            }
            return list;
        }

        /// <summary>js formatEpisodes — "E01 • E02"; empty string when empty.</summary>
        private static string FormatEpisodes(List<object> episodes)
        {
            return string.Join(" • ", episodes.Select(entry => "E" + TwoDigits((long)entry)));
        }

        /// <summary>js formatSeasons — "S01 • S02"; empty string when empty.</summary>
        private static string FormatSeasons(List<object> seasons)
        {
            return string.Join(" • ", seasons.Select(entry => "S" + TwoDigits((long)entry)));
        }

        /// <summary>js languageEmoji.</summary>
        private static string LanguageEmoji(object value)
        {
            switch (((value as string) ?? "").ToLowerInvariant())
            {
                case "en":
                case "eng":
                case "english":
                    return "🇬🇧";
                case "hi":
                case "hin":
                case "hindi":
                case "ml":
                case "mal":
                case "malayalam":
                case "ta":
                case "tam":
                case "tamil":
                case "te":
                case "tel":
                case "telugu":
                    return "🇮🇳";
                case "ja":
                case "jpn":
                case "japanese":
                    return "🇯🇵";
                case "ko":
                case "kor":
                case "korean":
                    return "🇰🇷";
                case "fr":
                case "fre":
                case "fra":
                case "french":
                    return "🇫🇷";
                case "es":
                case "spa":
                case "spanish":
                    return "🇪🇸";
                case "de":
                case "ger":
                case "deu":
                case "german":
                    return "🇩🇪";
                case "it":
                case "ita":
                case "italian":
                    return "🇮🇹";
                case "pt-br":
                case "ptbr":
                case "br":
                case "brazilian portuguese":
                case "portuguese brazilian":
                    return "🇧🇷";
                case "pt":
                case "por":
                case "portuguese":
                    return "🇵🇹";
                case "multi":
                    return "Multi";
                default:
                    return (value as string) ?? "";
            }
        }

        /// <summary>js streamType.</summary>
        private static string StreamType(DebridStream stream, DebridClientResolve resolve)
        {
            if (stream.DebridCacheStatus != null)
            {
                return "Debrid";
            }
            var type = (resolve.Type ?? "").ToLowerInvariant();
            if (type == "debrid")
            {
                return "Debrid";
            }
            if (type == "torrent")
            {
                return "p2p";
            }
            return resolve.Type ?? "";
        }

        /// <summary>js serviceCached.</summary>
        private static bool? ServiceCached(DebridStream stream, DebridClientResolve resolve)
        {
            var state = stream.DebridCacheStatus != null ? stream.DebridCacheStatus.State : null;
            if (state == Models.DebridCacheStatus.StateCached)
            {
                return true;
            }
            if (state == Models.DebridCacheStatus.StateNotCached)
            {
                return false;
            }
            return resolve.IsCached;
        }

        private static object BoxNumber(int? value)
        {
            return value.HasValue ? (object)(long)value.Value : null;
        }

        private static object BoxNumber(long? value)
        {
            return value.HasValue ? (object)value.Value : null;
        }

        private static object BoxSize(long? value)
        {
            return value.HasValue ? (object)value.Value : null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            return null;
        }
    }
}
