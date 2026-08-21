using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Networking;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Narrow seam over ProfileManager.getActiveProfileId (js/core/profile/profileManager.js).
    /// The controller wires the real ProfileManager-backed implementation at integration
    /// time; tests stub it so sync slices never take a compile-order dependency on
    /// ProfileManager.
    /// </summary>
    public interface IProfileIdProvider
    {
        string GetActiveProfileId();
    }

    /// <summary>Null-safe default: always profile "1".</summary>
    public sealed class DefaultProfileIdProvider : IProfileIdProvider
    {
        public string GetActiveProfileId()
        {
            return "1";
        }
    }

    /// <summary>JS parity: getSyncClientId (js/core/sync/syncClientIdentity.js).</summary>
    public interface ISyncClientIdProvider
    {
        Task<string> GetClientIdAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Default client-id provider; delegates to the shared SyncClientIdentity port of
    /// js/core/sync/syncClientIdentity.js so identity logic lives in exactly one place.
    /// </summary>
    public sealed class StoredSyncClientIdProvider : ISyncClientIdProvider
    {
        private readonly IKeyValueStore _store;

        public StoredSyncClientIdProvider(IKeyValueStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public Task<string> GetClientIdAsync(CancellationToken ct = default)
        {
            return SyncClientIdentity.GetSyncClientIdAsync(_store);
        }
    }

    /// <summary>
    /// Delete seam matching the additive SupabaseClient.DeleteAsync(table, filter, useSession, ct)
    /// method (JS parity supabaseApi.js:50-56). Sync services depend on this delegate so they
    /// compile standalone; the controller binds it to the real client at integration time.
    /// </summary>
    public delegate Task SupabaseTableDelete(string table, string filter, bool useSession, CancellationToken ct);

    /// <summary>Profile id parsing shared by every sync service (js resolveProfileId).</summary>
    public static class SyncProfileIds
    {
        /// <summary>Number(value): finite &gt; 0 → trunc, else 1.</summary>
        public static long Parse(string value)
        {
            var trimmed = (value ?? "").Trim();
            long id;
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0)
            {
                return id;
            }
            return 1;
        }

        /// <summary>Explicit argument wins; otherwise the provider's active profile id.</summary>
        public static long Resolve(long? profileId, IProfileIdProvider provider)
        {
            if (profileId.HasValue)
            {
                return profileId.Value > 0 ? profileId.Value : 1;
            }
            return Parse(provider != null ? provider.GetActiveProfileId() : null);
        }
    }

    /// <summary>
    /// Error classifiers ported verbatim from the JS sync services:
    /// isMissingResourceError / shouldTryLegacyTable / isOnConflictConstraintError.
    /// </summary>
    public static class SyncErrorClassifier
    {
        // PGRST205/PGRST202, 404, or PostgREST "could not find the table/function" messages.
        public static bool IsMissingResourceError(Exception error)
        {
            if (error == null)
            {
                return false;
            }
            if (IsLegacyTableCandidate(error))
            {
                return true;
            }
            var message = error.Message ?? "";
            return message.Contains("Could not find the table") ||
                   message.Contains("Could not find the function");
        }

        // pluginSyncService.shouldTryLegacyTable: identical trigger set.
        public static bool ShouldTryLegacyTable(Exception error)
        {
            return IsMissingResourceError(error);
        }

        // librarySyncService.isOnConflictConstraintError.
        public static bool IsOnConflictConstraintError(Exception error)
        {
            if (error == null)
            {
                return false;
            }
            var http = error as NuvioHttpException;
            if (http != null && http.Code == "42P10")
            {
                return true;
            }
            var message = error.Message ?? "";
            return message.Contains("42P10") ||
                   message.Contains(
                       "no unique or exclusion constraint matching the ON CONFLICT specification");
        }

        private static bool IsLegacyTableCandidate(Exception error)
        {
            var http = error as NuvioHttpException;
            if (http != null)
            {
                if (http.Status == 404)
                {
                    return true;
                }
                if (http.Code == "PGRST205" || http.Code == "PGRST202")
                {
                    return true;
                }
            }
            var message = error.Message ?? "";
            return message.Contains("PGRST205") || message.Contains("PGRST202");
        }
    }

    /// <summary>
    /// Raw {version:1, profiles:{...}} envelope access for hand-rolled stores that do NOT go
    /// through createProfileScopedStore in JS (traktAuthState / simklAuthState). Unlike
    /// ProfileScopedStore, a missing profile reads back empty instead of being seeded from
    /// profile "1" — matching js/data/local/traktAuthStore.js readEnvelope semantics.
    /// </summary>
    public static class RawProfileEnvelope
    {
        public static async Task<Dictionary<string, JsonElement>> GetProfileAsync(
            IKeyValueStore store, string key, string profileId)
        {
            var raw = store == null ? null : await store.GetAsync(key).ConfigureAwait(false);
            if (string.IsNullOrEmpty(raw))
            {
                return new Dictionary<string, JsonElement>();
            }
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("profiles", out var profiles) ||
                        profiles.ValueKind != JsonValueKind.Object ||
                        !profiles.TryGetProperty(NormalizeKey(profileId), out var value) ||
                        value.ValueKind != JsonValueKind.Object)
                    {
                        return new Dictionary<string, JsonElement>();
                    }
                    return DeserializeObject(value);
                }
            }
            catch (JsonException)
            {
                return new Dictionary<string, JsonElement>();
            }
        }

        public static async Task SetProfileAsync(
            IKeyValueStore store, string key, string profileId,
            Dictionary<string, JsonElement> state)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            Dictionary<string, JsonElement> profiles;
            var raw = await store.GetAsync(key).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(raw))
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("profiles", out var existing) &&
                            existing.ValueKind == JsonValueKind.Object)
                        {
                            profiles = DeserializeObject(existing);
                        }
                        else
                        {
                            profiles = new Dictionary<string, JsonElement>();
                        }
                    }
                }
                catch (JsonException)
                {
                    profiles = new Dictionary<string, JsonElement>();
                }
            }
            else
            {
                profiles = new Dictionary<string, JsonElement>();
            }

            profiles[NormalizeKey(profileId)] =
                JsonSerializer.SerializeToElement(state ?? new Dictionary<string, JsonElement>());
            var envelope = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["profiles"] = profiles
            };
            await store.SetAsync(key, JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
        }

        private static string NormalizeKey(string profileId)
        {
            var raw = (profileId ?? "").Trim();
            return raw.Length > 0 ? raw : "1";
        }

        internal static Dictionary<string, JsonElement> DeserializeObject(JsonElement element)
        {
            var result = new Dictionary<string, JsonElement>();
            foreach (var property in element.EnumerateObject())
            {
                result[property.Name] = property.Value.Clone();
            }
            return result;
        }
    }

    /// <summary>
    /// Profile-scoped JSON object store factory shared by the credential/collection sync
    /// services: shallow-merging partial writes over the stored object, preserving fields the
    /// sync service itself does not manage.
    /// </summary>
    public static class ScopedJsonStore
    {
        public static ProfileScopedStore<Dictionary<string, JsonElement>> Create(
            string key, IKeyValueStore store)
        {
            return ProfileScopedStore<Dictionary<string, JsonElement>>.CreateWithStore(
                key,
                store,
                normalize: value => value ?? new Dictionary<string, JsonElement>(),
                merge: (current, partial) =>
                {
                    var merged = current ?? new Dictionary<string, JsonElement>();
                    foreach (var entry in partial ?? new Dictionary<string, JsonElement>())
                    {
                        merged[entry.Key] = entry.Value;
                    }
                    return merged;
                });
        }

        // JS String(v || "") semantics for flat settings maps.
        public static string GetString(IReadOnlyDictionary<string, JsonElement> obj, string key)
        {
            JsonElement element;
            if (obj == null || !obj.TryGetValue(key, out element))
            {
                return "";
            }
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetRawText();
                default:
                    return "";
            }
        }

        public static void SetString(
            IDictionary<string, JsonElement> target, string key, string value)
        {
            target[key] = JsonSerializer.SerializeToElement(value ?? "");
        }

        public static void SetNumber(
            IDictionary<string, JsonElement> target, string key, long? value)
        {
            if (value.HasValue)
            {
                target[key] = JsonSerializer.SerializeToElement(value.Value);
            }
            else
            {
                target[key] = JsonDocument.Parse("null").RootElement.Clone();
            }
        }
    }
}
