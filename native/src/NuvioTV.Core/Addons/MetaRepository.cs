using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Models;

namespace NuvioTV.Core.Addons
{
    /// <summary>
    /// Meta fetcher with per-URL cache + in-flight dedupe and cross-addon
    /// resolution. Verbatim port of js/data/repository/metaRepository.js.
    /// JS returns {status:"success"|"error"} envelopes; here success maps to a
    /// non-null Models.Meta and every failure path (transport error, missing
    /// meta, no candidate addon) maps to null.
    /// </summary>
    public sealed class MetaRepository
    {
        private readonly StremioAddonClient _client;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Meta> _metaCache = new Dictionary<string, Meta>();
        private readonly Dictionary<string, Task<Meta>> _inFlightMeta = new Dictionary<string, Task<Meta>>();
        private readonly Dictionary<string, Task<Meta>> _inFlightMetaAll = new Dictionary<string, Task<Meta>>();

        public MetaRepository(StremioAddonClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>js getMeta(addonBaseUrl, type, id) → Meta or null.</summary>
        public Task<Meta> GetAsync(string addonBaseUrl, string type, string id, CancellationToken ct = default)
        {
            var normalizedType = (type ?? "").Trim();
            var normalizedId = (id ?? "").Trim();
            var canonicalBaseUrl = AddonUrlBuilder.CanonicalizeUrl(addonBaseUrl);
            var cacheKey = $"{canonicalBaseUrl}:{normalizedType}:{normalizedId}";

            lock (_lock)
            {
                if (_metaCache.TryGetValue(cacheKey, out var cached))
                {
                    return Task.FromResult(cached);
                }
                if (_inFlightMeta.TryGetValue(cacheKey, out var inFlight))
                {
                    return inFlight;
                }
            }

            var request = RequestMetaAsync(addonBaseUrl, normalizedType, normalizedId, cacheKey);
            lock (_lock)
            {
                // Another caller may have won the race while we built the task.
                if (_inFlightMeta.TryGetValue(cacheKey, out var existing))
                {
                    return existing;
                }
                _inFlightMeta[cacheKey] = request;
            }
            return request;
        }

        private async Task<Meta> RequestMetaAsync(
            string addonBaseUrl, string normalizedType, string normalizedId, string cacheKey)
        {
            try
            {
                JsonElement payload;
                try
                {
                    payload = await _client.FetchMetaAsync(
                        addonBaseUrl, normalizedType, normalizedId).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // js safeApiCall → { status: "error" }.
                    return null;
                }

                var metaElement = StremioJson.TryGet(payload, "meta", out var found) ? found : default;
                var meta = MapMeta(metaElement);
                if (meta == null)
                {
                    // js: { status: "error", message: "Meta not found", code: 404 }.
                    return null;
                }

                lock (_lock)
                {
                    _metaCache[cacheKey] = meta;
                }
                return meta;
            }
            finally
            {
                lock (_lock)
                {
                    _inFlightMeta.Remove(cacheKey);
                }
            }
        }

        /// <summary>
        /// js getMetaFromAllAddons(type, id): resolve against installed addons,
        /// preferring explicit idPrefix owners, then the requested type, then the
        /// canonical type inferred from the id. First success wins; null otherwise.
        /// </summary>
        public async Task<Meta> GetFromAllAddonsAsync(
            string type, string id, IReadOnlyList<Addon> addons, CancellationToken ct = default)
        {
            var requestedType = (type ?? "").Trim();
            var inferredType = InferCanonicalType(requestedType, id);
            var cleanId = (id ?? "").Trim();
            var cacheKey = $"all:{requestedType}:{inferredType}:{cleanId}";

            Task<Meta> effective;
            lock (_lock)
            {
                if (_metaCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
                if (!_inFlightMetaAll.TryGetValue(cacheKey, out effective))
                {
                    var request = RequestMetaFromAllAddonsAsync(
                        requestedType, inferredType, cleanId, cacheKey, addons, ct);
                    _inFlightMetaAll[cacheKey] = request;
                    effective = request;
                }
            }
            return await effective.ConfigureAwait(false);
        }

        private async Task<Meta> RequestMetaFromAllAddonsAsync(
            string requestedType, string inferredType, string id, string cacheKey,
            IReadOnlyList<Addon> addons, CancellationToken ct)
        {
            try
            {
                var candidates = new List<Candidate>();
                var seenCandidates = new HashSet<string>(StringComparer.Ordinal);

                void AddCandidate(Addon addon, string candidateType)
                {
                    var cleanType = (candidateType ?? "").Trim();
                    if (addon == null || cleanType.Length == 0)
                    {
                        return;
                    }
                    var key = $"{addon.BaseUrl}::{cleanType}";
                    if (!seenCandidates.Add(key))
                    {
                        return;
                    }
                    candidates.Add(new Candidate(addon, cleanType));
                }

                // Prefer addons whose explicit idPrefixes identify them as the owner.
                // This also safely recovers `tv` when a secondary catalog forwarded a
                // broader row type such as `channel`.
                foreach (var addon in SafeAddons(addons))
                {
                    var hasMatchingPrefix = (addon.Resources ?? new List<AddonResource>()).Any(resource =>
                        string.Equals((resource?.Name ?? "").Trim().ToLowerInvariant(), "meta", StringComparison.Ordinal) &&
                        AddonRepository.GetResourceIdPrefixes(addon, resource).Count > 0 &&
                        AddonRepository.ResourceSupportsId(addon, resource, id, caseInsensitive: true));
                    if (!hasMatchingPrefix)
                    {
                        continue;
                    }
                    var ownerType = AddonRepository.ResolveResourceRequestType(
                        addon, "meta", requestedType, id,
                        caseInsensitive: true, allowIdTypeFallback: true);
                    if (!string.IsNullOrEmpty(ownerType))
                    {
                        AddCandidate(addon, ownerType);
                    }
                }

                foreach (var addon in SafeAddons(addons))
                {
                    var candidateType = AddonRepository.ResolveResourceRequestType(
                        addon, "meta", requestedType, id, caseInsensitive: true);
                    if (!string.IsNullOrEmpty(candidateType))
                    {
                        AddCandidate(addon, candidateType);
                    }
                }

                if (!string.Equals(inferredType, requestedType, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var addon in SafeAddons(addons))
                    {
                        var candidateType = AddonRepository.ResolveResourceRequestType(
                            addon, "meta", inferredType, id, caseInsensitive: true);
                        if (!string.IsNullOrEmpty(candidateType))
                        {
                            AddCandidate(addon, candidateType);
                        }
                    }
                }

                foreach (var candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await GetAsync(candidate.Addon.BaseUrl, candidate.Type, id, ct)
                        .ConfigureAwait(false);
                    if (result != null)
                    {
                        lock (_lock)
                        {
                            _metaCache[cacheKey] = result;
                        }
                        return result;
                    }
                }

                // js: { status: "error", message: "Meta not found in installed addons", code: 404 }.
                return null;
            }
            finally
            {
                lock (_lock)
                {
                    _inFlightMetaAll.Remove(cacheKey);
                }
            }
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _metaCache.Clear();
                _inFlightMeta.Clear();
                _inFlightMetaAll.Clear();
            }
        }

        // metaRepository.js:168-181 inferCanonicalType.
        public static string InferCanonicalType(string type, string id)
        {
            var normalizedType = (type ?? "").Trim();
            var lowerType = normalizedType.ToLowerInvariant();
            var known = new[] { "movie", "series", "channel", "tv", "anime" };
            if (known.Contains(lowerType))
            {
                return normalizedType;
            }
            var normalizedId = (id ?? "").ToLowerInvariant();
            if (normalizedId.Contains(":movie:")) return "movie";
            if (normalizedId.Contains(":series:")) return "series";
            if (normalizedId.Contains(":tv:")) return "tv";
            if (normalizedId.Contains(":anime:")) return "anime";
            return normalizedType;
        }

        // metaRepository.js:187-212 mapMeta — rejects empty payloads, normalizes
        // display text on name/description/releaseInfo/genres.
        internal static Meta MapMeta(JsonElement meta)
        {
            if (meta.ValueKind != JsonValueKind.Object || IsEmptyObject(meta))
            {
                return null;
            }

            var genres = StremioJson.Elements(StremioJson.ArrayOrEmpty(meta, "genres"))
                .Select(genre => StremioJson.NormalizeDisplayText(
                    genre.ValueKind == JsonValueKind.String ? genre.GetString() : ""))
                .ToList();

            var videos = new List<MetaVideo>();
            foreach (var video in StremioJson.Elements(StremioJson.ArrayOrEmpty(meta, "videos")))
            {
                videos.Add(StremioJson.Deserialize<MetaVideo>(video));
            }

            var name = StremioJson.Str(meta, "name");
            var description = StremioJson.Str(meta, "description");
            var releaseInfo = StremioJson.Str(meta, "releaseInfo");

            return new Meta(
                StremioJson.Str(meta, "id"),
                StremioJson.Str(meta, "type"),
                string.IsNullOrEmpty(name) ? "Untitled" : StremioJson.NormalizeDisplayText(name),
                OrNull(StremioJson.StrOrNull(meta, "poster")),
                OrNull(StremioJson.StrOrNull(meta, "background")),
                OrNull(StremioJson.StrOrNull(meta, "logo")),
                StremioJson.NormalizeDisplayText(description),
                genres,
                videos,
                StremioJson.NormalizeDisplayText(releaseInfo));
        }

        private static bool IsEmptyObject(JsonElement element)
        {
            using var enumerator = element.EnumerateObject();
            return !enumerator.MoveNext();
        }

        private static string OrNull(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static IEnumerable<Addon> SafeAddons(IReadOnlyList<Addon> addons)
        {
            return addons ?? (IEnumerable<Addon>)Array.Empty<Addon>();
        }

        private readonly struct Candidate
        {
            public readonly Addon Addon;
            public readonly string Type;

            public Candidate(Addon addon, string type)
            {
                Addon = addon;
                Type = type;
            }
        }
    }
}
