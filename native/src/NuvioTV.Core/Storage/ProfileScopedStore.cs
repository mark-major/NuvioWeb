using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// Profile-scoped store that migrates legacy unscoped values and isolates data per profile.
    /// Ports js/data/local/profileScopedStore.js semantics.
    /// </summary>
    /// <typeparam name="T">The type of value stored per profile.</typeparam>
    public sealed class ProfileScopedStore<T>
    {
        private const string ProfileScopedMarker = "__profileScoped";
        private const int CurrentProfileScopedVersion = 1;
        private const string DefaultProfileId = "1";
        private const string ProfilesStorageKey = "profiles"; // Matches webapp PROFILES_KEY

        private readonly string _key;
        private readonly Func<T, T> _normalize;
        private readonly Func<T, T, T> _merge;
        private readonly IKeyValueStore _store;

        /// <summary>
        /// Internal envelope structure for profile-scoped storage.
        /// </summary>
        private class ProfileScopedEnvelope
        {
            [JsonPropertyName(ProfileScopedMarker)]
            public bool IsProfileScoped { get; set; }

            [JsonPropertyName("version")]
            public int Version { get; set; }

            [JsonPropertyName("profiles")]
            public Dictionary<string, JsonElement> Profiles { get; set; }
        }

        private ProfileScopedStore(string key, IKeyValueStore store, Func<T, T> normalize, Func<T, T, T> merge)
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _normalize = normalize ?? (item => item);
            _merge = merge ?? ((current, partial) =>
            {
                // Default merge behavior for reference types
                if (current == null) return partial;
                if (partial == null) return current;
                
                // For objects, this is a simple implementation - callers should provide custom merge logic
                var defaultMerge = JsonSerializer.Serialize(partial, LocalStore.JsonOptions);
                return JsonSerializer.Deserialize<T>(defaultMerge, LocalStore.JsonOptions);
            });
        }

        /// <summary>
        /// Creates a profile-scoped store with the specified key, normalize, and merge functions.
        /// </summary>
        /// <param name="key">The storage key.</param>
        /// <param name="normalize">Function to normalize values (e.g., set defaults).</param>
        /// <param name="merge">Function to merge partial updates into existing values.</param>
        public static ProfileScopedStore<T> Create(string key, Func<T, T> normalize, Func<T, T, T> merge)
        {
            return new ProfileScopedStore<T>(key, new InMemoryKeyValueStore(), normalize, merge);
        }

        /// <summary>
        /// Creates a profile-scoped store with a custom IKeyValueStore implementation.
        /// </summary>
        public static ProfileScopedStore<T> CreateWithStore(string key, IKeyValueStore store, Func<T, T> normalize, Func<T, T, T> merge)
        {
            return new ProfileScopedStore<T>(key, store, normalize, merge);
        }

        /// <summary>
        /// Normalizes a profile ID to a non-empty string, defaulting to "1".
        /// </summary>
        private static string NormalizeProfileId(string profileId)
        {
            var raw = string.IsNullOrEmpty(profileId) ? DefaultProfileId : profileId.Trim();
            return string.IsNullOrEmpty(raw) ? DefaultProfileId : raw;
        }
        /// <summary>
        /// Gets all known profile IDs from the profiles storage, ensuring "1" is included.
        /// Matches js/data/local/profileScopedStore.js getKnownProfileIds() semantics.
        /// </summary>
        private static async Task<string[]> GetKnownProfileIdsAsync(IKeyValueStore store)
        {
            try
            {
                var profilesJson = await store.GetAsync(ProfilesStorageKey);
                if (string.IsNullOrEmpty(profilesJson))
                {
                    return new[] { DefaultProfileId };
                }

                // Deserialize profiles array and extract IDs
                var profiles = JsonSerializer.Deserialize<JsonElement[]>(profilesJson, LocalStore.JsonOptions);
                var ids = new List<string>();

                if (profiles != null)
                {
                    foreach (var profile in profiles)
                    {
                        // Extract id or profileIndex field
                        string id = null;
                        if (profile.ValueKind == JsonValueKind.Object)
                        {
                            if (profile.TryGetProperty("id", out var idProperty))
                            {
                                id = idProperty.GetString();
                            }
                            else if (profile.TryGetProperty("profileIndex", out var indexProperty))
                            {
                                id = indexProperty.GetString();
                            }
                        }
                        else if (profile.ValueKind == JsonValueKind.String)
                        {
                            id = profile.GetString();
                        }

                        if (!string.IsNullOrEmpty(id))
                        {
                            var trimmedId = id.Trim();
                            if (!string.IsNullOrEmpty(trimmedId))
                            {
                                ids.Add(trimmedId);
                            }
                        }
                    }
                }

                // Ensure "1" is first
                if (!ids.Contains(DefaultProfileId))
                {
                    ids.Insert(0, DefaultProfileId);
                }

                return ids.Distinct().ToArray();
            }
            catch
            {
                // On any error, return default profile
                return new[] { DefaultProfileId };
            }
        }


        /// <summary>
        /// Deep clones a value using JSON serialization.
        /// </summary>
        private T CloneValue(T value)
        {
            if (value == null)
            {
                return default;
            }

            var json = JsonSerializer.Serialize(value, LocalStore.JsonOptions);
            return JsonSerializer.Deserialize<T>(json, LocalStore.JsonOptions);
        }

        /// <summary>
        /// Checks if a value is a valid profile-scoped envelope.
        /// </summary>
        private bool IsProfileScopedEnvelope(ProfileScopedEnvelope envelope)
        {
            return envelope != null &&
                   envelope.IsProfileScoped &&
                   envelope.Version == CurrentProfileScopedVersion &&
                   envelope.Profiles != null;
        }

        /// <summary>
        /// Creates an empty envelope structure.
        /// </summary>
        private ProfileScopedEnvelope CreateEmptyEnvelope()
        {
            return new ProfileScopedEnvelope
            {
                IsProfileScoped = true,
                Version = CurrentProfileScopedVersion,
                Profiles = new Dictionary<string, JsonElement>()
            };
        }

        /// <summary>
        /// Normalizes all profile values in an envelope using the normalize function.
        /// </summary>
        private Dictionary<string, JsonElement> NormalizeEnvelopeProfiles(Dictionary<string, JsonElement> profiles)
        {
            var normalized = new Dictionary<string, JsonElement>();
            
            if (profiles == null)
            {
                return normalized;
            }

            foreach (var entry in profiles)
            {
                var normalizedProfileId = NormalizeProfileId(entry.Key);
                
                // Deserialize, normalize, and re-serialize
                var value = JsonSerializer.Deserialize<T>(entry.Value.GetRawText(), LocalStore.JsonOptions);
                var normalizedValue = _normalize(value ?? default);
                
                var normalizedJson = JsonSerializer.SerializeToElement(normalizedValue, LocalStore.JsonOptions);
                normalized[normalizedProfileId] = normalizedJson;
            }

            return normalized;
        }

        /// <summary>
        /// Reads the envelope from storage, handling legacy migration and normalization.
        /// </summary>
        private async Task<ProfileScopedEnvelope> ReadEnvelopeAsync()
        {
            var rawJson = await _store.GetAsync(_key);

            // Try to parse as envelope
            ProfileScopedEnvelope envelope = null;
            if (!string.IsNullOrEmpty(rawJson))
            {
                try
                {
                    envelope = JsonSerializer.Deserialize<ProfileScopedEnvelope>(rawJson, LocalStore.JsonOptions);
                }
                catch (JsonException)
                {
                    // Invalid JSON, treat as null for migration
                }
            }

            if (IsProfileScopedEnvelope(envelope))
            {
                // Normalize profiles if needed
                var normalizedProfiles = NormalizeEnvelopeProfiles(envelope.Profiles);
                var needsUpdate = !envelope.Profiles.Keys.SequenceEqual(normalizedProfiles.Keys);
                
                envelope.Profiles = normalizedProfiles;

                if (needsUpdate)
                {
                    var updatedJson = JsonSerializer.Serialize(envelope, LocalStore.JsonOptions);
                    await _store.SetAsync(_key, updatedJson);
                }

                return envelope;
            }

            // Legacy migration or empty storage
            if (string.IsNullOrEmpty(rawJson))
            {
                return CreateEmptyEnvelope();
            }

            // Migrate legacy unscoped value to all known profiles
            var legacyValue = JsonSerializer.Deserialize<T>(rawJson, LocalStore.JsonOptions);
            var normalizedLegacy = _normalize(legacyValue ?? default);
            
            var migrated = CreateEmptyEnvelope();
            
            // Get all known profile IDs (matches webapp getKnownProfileIds semantics)
            var profileIds = await GetKnownProfileIdsAsync(_store);
            
            foreach (var profileId in profileIds)
            {
                var clonedValue = CloneValue(normalizedLegacy);
                migrated.Profiles[profileId] = JsonSerializer.SerializeToElement(clonedValue, LocalStore.JsonOptions);
            }

            var migratedJson = JsonSerializer.Serialize(migrated, LocalStore.JsonOptions);
            await _store.SetAsync(_key, migratedJson);

            return migrated;
        }

        /// <summary>
        /// Persists the envelope to storage.
        /// </summary>
        private async Task PersistEnvelopeAsync(ProfileScopedEnvelope envelope)
        {
            var json = JsonSerializer.Serialize(envelope, LocalStore.JsonOptions);
            await _store.SetAsync(_key, json);
        }

        /// <summary>
        /// Ensures a profile has a value in the envelope, using profile "1" as seed if needed.
        /// </summary>
        private async Task<T> EnsureProfileValueAsync(ProfileScopedEnvelope envelope, string profileId)
        {
            var normalizedProfileId = NormalizeProfileId(profileId);
            
            if (envelope.Profiles.ContainsKey(normalizedProfileId))
            {
                var element = envelope.Profiles[normalizedProfileId];
                return JsonSerializer.Deserialize<T>(element.GetRawText(), LocalStore.JsonOptions);
            }

            // Use profile "1" as seed, or create empty
            T seed = default;
            if (envelope.Profiles.ContainsKey(DefaultProfileId))
            {
                var primaryElement = envelope.Profiles[DefaultProfileId];
                seed = JsonSerializer.Deserialize<T>(primaryElement.GetRawText(), LocalStore.JsonOptions);
            }

            var clonedSeed = CloneValue(seed ?? default);
            var normalizedSeed = _normalize(clonedSeed ?? default);
            
            envelope.Profiles[normalizedProfileId] = JsonSerializer.SerializeToElement(normalizedSeed, LocalStore.JsonOptions);
            await PersistEnvelopeAsync(envelope);

            return normalizedSeed;
        }

        /// <summary>
        /// Gets the value for a specific profile ID.
        /// </summary>
        public async Task<T> GetAsync(string profileId)
        {
            var envelope = await ReadEnvelopeAsync();
            return await EnsureProfileValueAsync(envelope, profileId);
        }

        /// <summary>
        /// Gets the value for the default profile (profile "1").
        /// </summary>
        public Task<T> GetAsync()
        {
            return GetAsync(DefaultProfileId);
        }

        /// <summary>
        /// Replaces the value for a specific profile with a new value.
        /// </summary>
        public async Task<T> ReplaceAsync(string profileId, T nextValue)
        {
            var envelope = await ReadEnvelopeAsync();
            var normalizedProfileId = NormalizeProfileId(profileId);
            
            var clonedValue = CloneValue(nextValue);
            var normalizedValue = _normalize(clonedValue ?? default);
            
            envelope.Profiles[normalizedProfileId] = JsonSerializer.SerializeToElement(normalizedValue, LocalStore.JsonOptions);
            await PersistEnvelopeAsync(envelope);

            return normalizedValue;
        }

        /// <summary>
        /// Merges a partial update into the existing value for a specific profile.
        /// </summary>
        public async Task<T> SetAsync(string profileId, T partial)
        {
            var current = await GetAsync(profileId);
            var merged = _merge(current, partial);
            return await ReplaceAsync(profileId, merged);
        }

        /// <summary>
        /// Merges a partial update into the default profile (profile "1").
        /// </summary>
        public Task<T> SetAsync(T partial)
        {
            return SetAsync(DefaultProfileId, partial);
        }

        /// <summary>
        /// Clears the value for a specific profile.
        /// </summary>
        public async Task ClearProfileAsync(string profileId)
        {
            var envelope = await ReadEnvelopeAsync();
            var normalizedProfileId = NormalizeProfileId(profileId);
            
            envelope.Profiles.Remove(normalizedProfileId);
            await PersistEnvelopeAsync(envelope);
        }

        /// <summary>
        /// Subscribes to changes for this store (placeholder for future change notification).
        /// </summary>
        public Task SubscribeAsync(Action onChanged)
        {
            // Placeholder for future change notification support
            return Task.CompletedTask;
        }
    }
}
