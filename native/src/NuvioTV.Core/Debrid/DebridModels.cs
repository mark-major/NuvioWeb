using System;
using System.Collections.Generic;

namespace NuvioTV.Core.Debrid
{
    /// <summary>
    /// Parsed torrent/release metadata attached to a debrid resolve
    /// (js clientResolve.stream.raw.parsed).
    /// </summary>
    public sealed class DebridParsedMetadata
    {
        public string RawTitle { get; set; }
        public string ParsedTitle { get; set; }
        public int? Year { get; set; }
        public IReadOnlyList<int> Seasons { get; set; }
        public IReadOnlyList<int> Episodes { get; set; }
        public IReadOnlyList<string> Hdr { get; set; }
        public string BitDepth { get; set; }
        public IReadOnlyList<string> Audio { get; set; }
        public IReadOnlyList<string> Channels { get; set; }
        public IReadOnlyList<string> Languages { get; set; }
        public string Resolution { get; set; }
        public string Quality { get; set; }
        public string Codec { get; set; }
        public string Network { get; set; }
        public string Group { get; set; }
        public double? Duration { get; set; }
        public string Edition { get; set; }

        public DebridParsedMetadata()
        {
            Seasons = new List<int>();
            Episodes = new List<int>();
            Hdr = new List<string>();
            Audio = new List<string>();
            Channels = new List<string>();
            Languages = new List<string>();
        }
    }

    /// <summary>Raw addon stream metadata (js clientResolve.stream.raw).</summary>
    public sealed class DebridResolveRawMetadata
    {
        public string Filename { get; set; }
        public string TorrentName { get; set; }
        public long? Size { get; set; }
        public long? FolderSize { get; set; }
        public string Indexer { get; set; }
        public string Tracker { get; set; }
        public string Network { get; set; }
        public DebridParsedMetadata Parsed { get; set; }
    }

    /// <summary>Resolve-side stream wrapper (js clientResolve.stream).</summary>
    public sealed class DebridResolveStreamSource
    {
        public DebridResolveRawMetadata Raw { get; set; }
    }

    /// <summary>
    /// Client-resolve descriptor. Superset of Models.ClientResolve with the
    /// fields the debrid stack reads in JS: serviceExtension, title, season,
    /// episode, isCached and the parsed raw metadata tree.
    /// Source: js/core/debrid/directDebridResolver.js buildLocalResolve.
    /// </summary>
    public sealed class DebridClientResolve
    {
        public string Type { get; set; }
        public string Service { get; set; }
        public string ServiceExtension { get; set; }
        public string InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string Filename { get; set; }
        public string TorrentName { get; set; }
        public string MagnetUri { get; set; }
        public string Title { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public bool? IsCached { get; set; }
        public IReadOnlyList<string> Sources { get; set; }
        public DebridResolveStreamSource Stream { get; set; }

        public DebridClientResolve()
        {
            Sources = new List<string>();
        }
    }

    /// <summary>
    /// Stream shape consumed by the debrid stack. Mirrors the JS stream object
    /// fields read by directDebridResolver.js / debridFileSelection.js /
    /// buildTemplateValues. behaviorHints.filename / behaviorHints.videoSize are
    /// surfaced as FilenameHint / VideoSizeHint because the shared
    /// StreamBehaviorHints model does not carry them yet.
    /// </summary>
    public sealed class DebridStream
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public string ExternalUrl { get; set; }
        public string YtId { get; set; }
        public string InfoHash { get; set; }
        public int? FileIdx { get; set; }
        public string Quality { get; set; }
        public string AddonName { get; set; }
        public string AddonLogo { get; set; }
        public string RawAddonName { get; set; }
        public string FilenameHint { get; set; }
        public long? VideoSizeHint { get; set; }
        public Models.DebridCacheStatus DebridCacheStatus { get; set; }
        public IReadOnlyList<string> Sources { get; set; }
        public DebridClientResolve ClientResolve { get; set; }

        public DebridStream()
        {
            Sources = new List<string>();
        }
    }

    /// <summary>
    /// Pre-computed stream facts feeding template values
    /// (js directDebridStreamPresentation.js facts()). Values are enum ids
    /// ("P2160", "WEB_DL", "ATMOS", "CH_5_1", "EN", ...) or "UNKNOWN".
    /// </summary>
    public sealed class DebridStreamFact
    {
        public string Resolution { get; set; }
        public string Quality { get; set; }
        public string Codec { get; set; }
        public string ReleaseGroup { get; set; }
        public string Edition { get; set; }
        public IReadOnlyList<string> VisualTags { get; set; }
        public IReadOnlyList<string> AudioTags { get; set; }
        public IReadOnlyList<string> AudioChannels { get; set; }
        public IReadOnlyList<string> Languages { get; set; }

        public DebridStreamFact()
        {
            VisualTags = new List<string>();
            AudioTags = new List<string>();
            AudioChannels = new List<string>();
            Languages = new List<string>();
        }
    }

    /// <summary>Flat debrid settings snapshot the resolver reads (js DebridSettingsStore.get()).</summary>
    public sealed class DebridSettingsSnapshot
    {
        public bool Enabled { get; set; }
        public string TorboxApiKey { get; set; }
        public string PremiumizeApiKey { get; set; }
        public string RealDebridApiKey { get; set; }
        public string PreferredResolverProviderId { get; set; }

        public IReadOnlyDictionary<string, string> ToDictionary()
        {
            var map = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(TorboxApiKey))
            {
                map["torboxApiKey"] = TorboxApiKey;
            }
            if (!string.IsNullOrEmpty(PremiumizeApiKey))
            {
                map["premiumizeApiKey"] = PremiumizeApiKey;
            }
            if (!string.IsNullOrEmpty(RealDebridApiKey))
            {
                map["realDebridApiKey"] = RealDebridApiKey;
            }
            if (!string.IsNullOrEmpty(PreferredResolverProviderId))
            {
                map["preferredResolverProviderId"] = PreferredResolverProviderId;
            }
            return map;
        }
    }

    /// <summary>Settings seam — profile-scoped persistence lands with the sync tasks.</summary>
    public interface IDebridSettingsProvider
    {
        DebridSettingsSnapshot Get();
    }

    /// <summary>Resolve outcome status values (js failure()/success() status strings).</summary>
    public static class DebridResolveStatus
    {
        public const string Success = "success";
        public const string Stale = "stale";
        public const string NotCached = "not_cached";
        public const string Error = "error";
        public const string ServiceDegraded = "service_degraded";
        public const string Disabled = "disabled";
        public const string MissingApiKey = "missing_api_key";
    }

    /// <summary>Per-provider resolve result (js success()/failure() payload).</summary>
    public sealed class DebridResolveResult
    {
        public string Status { get; set; }
        public string Detail { get; set; }
        public string Url { get; set; }
        public string Filename { get; set; }
        public double? VideoSize { get; set; }

        public static DebridResolveResult Failure(string status, string detail = null)
        {
            return new DebridResolveResult { Status = status, Detail = detail };
        }

        public static DebridResolveResult Success(string url, string filename, double? videoSize)
        {
            return new DebridResolveResult
            {
                Status = DebridResolveStatus.Success,
                Url = url,
                Filename = filename,
                VideoSize = videoSize
            };
        }
    }

    /// <summary>Top-level resolver outcome (js {status:"success", stream} | failure).</summary>
    public sealed class DebridResolveOutcome
    {
        public string Status { get; set; }
        public string Detail { get; set; }
        public DebridStream Stream { get; set; }

        public static DebridResolveOutcome FromResult(DebridResolveResult result)
        {
            return new DebridResolveOutcome { Status = result.Status, Detail = result.Detail };
        }
    }
}
