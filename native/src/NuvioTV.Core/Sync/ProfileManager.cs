using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Models;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Local profile list management over the shared key-value store.
    ///
    /// Behavioral spec (do not diverge): js/core/profile/profileManager.js.
    /// Storage keys match the web app exactly ("profiles", "activeProfileId",
    /// "rememberLastProfile", "hasEverSelectedProfile") and profile JSON is
    /// camelCase via <see cref="LocalStore.JsonOptions"/>.
    /// </summary>
    public sealed class ProfileManager
    {
        public const int MaxProfiles = 6;

        private const string ProfilesKey = "profiles";
        private const string ActiveProfileIdKey = "activeProfileId";
        private const string RememberLastProfileKey = "rememberLastProfile";
        private const string HasEverSelectedProfileKey = "hasEverSelectedProfile";
        private const string DefaultAvatarColorHex = "#1E88E5";

        private static readonly UserProfile DefaultProfile = new UserProfile(
            "1", 1, "Profile 1", "#1E88E5", true, false, false, null, null);

        private static readonly IReadOnlyList<UserProfile> DefaultProfiles =
            new[] { DefaultProfile };

        private readonly IKeyValueStore _store;

        public ProfileManager(IKeyValueStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // -------------------------------------------------------------------
        // Normalization (JS parity: normalizeProfile, profileManager.js:13-31)
        // -------------------------------------------------------------------

        public static UserProfile NormalizeProfile(JsonElement profile, int index)
        {
            var fallbackIndex = index + 1;

            var rawIndex = SyncJson.GetNumber(profile, "profileIndex", "profile_index");
            if (!rawIndex.HasValue || rawIndex.Value <= 0)
            {
                var fromId = SyncJson.CoerceNumber(SyncJson.GetProperty(profile, "id"));
                if (fromId.HasValue && fromId.Value > 0)
                {
                    rawIndex = fromId;
                }
            }

            var normalizedIndex = rawIndex.HasValue && rawIndex.Value > 0
                ? (int)Math.Truncate(rawIndex.Value)
                : fallbackIndex;

            return new UserProfile(
                normalizedIndex.ToString(),
                normalizedIndex,
                Pick(SyncJson.GetStringOrNull(profile, "name"), ""),
                Pick(SyncJson.GetStringOrNull(profile, "avatarColorHex", "avatar_color_hex"), DefaultAvatarColorHex),
                SyncJson.GetBool(profile, "isPrimary").GetValueOrDefault(false) || normalizedIndex == 1,
                SyncJson.GetBool(profile, "usesPrimaryAddons").GetValueOrDefault(false),
                SyncJson.GetBool(profile, "usesPrimaryPlugins").GetValueOrDefault(false),
                FirstNonEmpty(SyncJson.GetStringOrNull(profile, "avatarId", "avatar_id"), null),
                NonEmptyTrimmed(FirstNonEmpty(SyncJson.GetStringOrNull(profile, "avatarUrl", "avatar_url"), null)));
        }

        public static UserProfile NormalizeProfile(UserProfile profile, int index)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var fallbackIndex = index + 1;
            var normalizedIndex = profile.ProfileIndex > 0
                ? profile.ProfileIndex
                : int.TryParse(profile.Id == null ? "" : profile.Id.Trim(), out var fromId) && fromId > 0
                    ? fromId
                    : fallbackIndex;
            if (normalizedIndex <= 0)
            {
                normalizedIndex = fallbackIndex;
            }

            return new UserProfile(
                normalizedIndex.ToString(),
                normalizedIndex,
                profile.Name ?? "",
                string.IsNullOrEmpty(profile.AvatarColorHex) ? DefaultAvatarColorHex : profile.AvatarColorHex,
                profile.IsPrimary || normalizedIndex == 1,
                profile.UsesPrimaryAddons,
                profile.UsesPrimaryPlugins,
                profile.AvatarId,
                string.IsNullOrEmpty(profile.AvatarUrl) || string.IsNullOrEmpty(profile.AvatarUrl.Trim())
                    ? null
                    : profile.AvatarUrl.Trim());
        }

        // -------------------------------------------------------------------
        // Profiles list (profileManager.js:51-71)
        // -------------------------------------------------------------------

        // JS parity: getProfiles — non-empty stored lists are re-normalized and
        // persisted back; empty storage seeds the default single profile.
        public async Task<IReadOnlyList<UserProfile>> GetProfilesAsync()
        {
            var stored = await ReadStoredProfilesAsync().ConfigureAwait(false);
            if (stored.Count > 0)
            {
                var normalized = new List<UserProfile>(stored.Count);
                for (var i = 0; i < stored.Count; i++)
                {
                    normalized.Add(NormalizeProfile(stored[i], i));
                }

                await WriteProfilesAsync(normalized).ConfigureAwait(false);
                return normalized;
            }

            await WriteProfilesAsync(DefaultProfiles).ConfigureAwait(false);
            return DefaultProfiles;
        }

        // JS parity: replaceProfiles.
        public async Task ReplaceProfilesAsync(IEnumerable<UserProfile> profiles)
        {
            var source = profiles ?? Enumerable.Empty<UserProfile>();
            var normalized = new List<UserProfile>();
            var index = 0;
            foreach (var profile in source)
            {
                normalized.Add(NormalizeProfile(profile, index));
                index++;
            }

            await WriteProfilesAsync(normalized).ConfigureAwait(false);
        }

        // JS parity: getNextProfileIndex / getFirstAvailableProfileIndex (2..MaxProfiles).
        public static int? GetNextProfileIndex(IEnumerable<UserProfile> profiles)
        {
            var used = new HashSet<int>();
            foreach (var profile in profiles ?? Enumerable.Empty<UserProfile>())
            {
                var candidate = profile.ProfileIndex > 0
                    ? profile.ProfileIndex
                    : int.TryParse(profile.Id == null ? "" : profile.Id.Trim(), out var parsed) && parsed > 0
                        ? parsed
                        : 0;
                if (candidate > 0)
                {
                    used.Add(candidate);
                }
            }

            for (var index = 2; index <= MaxProfiles; index++)
            {
                if (!used.Contains(index))
                {
                    return index;
                }
            }

            return null;
        }

        public async Task SetActiveProfileAsync(string id)
        {
            await LocalStore.SetAsync(ActiveProfileIdKey, id == null ? "" : id, _store).ConfigureAwait(false);
            await LocalStore.SetAsync(HasEverSelectedProfileKey, true, _store).ConfigureAwait(false);
        }

        // JS parity: isRememberLastProfileEnabled.
        public Task<bool> IsRememberLastProfileEnabledAsync()
        {
            return LocalStore.GetAsync<bool>(RememberLastProfileKey, _store, false);
        }

        // JS parity: hasEverSelectedProfile — explicit flag or any active id recorded.
        public async Task<bool> HasEverSelectedProfileAsync()
        {
            var flag = await LocalStore.GetAsync<bool>(HasEverSelectedProfileKey, _store, false).ConfigureAwait(false);
            if (flag)
            {
                return true;
            }

            var rawActive = await LocalStore.GetAsync<string>(ActiveProfileIdKey, _store).ConfigureAwait(false);
            return !string.IsNullOrEmpty(rawActive);
        }

        public Task ClearActiveProfileAsync()
        {
            return LocalStore.RemoveAsync(ActiveProfileIdKey, _store);
        }

        // JS parity: getActiveProfileId — missing value resolves to "1".
        public async Task<string> GetActiveProfileId()
        {
            var raw = await LocalStore.GetAsync<string>(ActiveProfileIdKey, _store).ConfigureAwait(false);
            return string.IsNullOrEmpty(raw) ? "1" : raw;
        }

        // -------------------------------------------------------------------
        // Mutations (profileManager.js:97-174)
        // -------------------------------------------------------------------

        // JS parity: createProfile — rejects empty names, enforces MaxProfiles,
        // allocates the first free index starting at 2.
        public async Task<bool> CreateProfileAsync(
            string name,
            string avatarColorHex = null,
            string avatarId = null,
            string avatarUrl = null,
            bool usesPrimaryAddons = false,
            bool usesPrimaryPlugins = false)
        {
            var trimmedName = (name ?? "").Trim();
            if (trimmedName.Length == 0)
            {
                return false;
            }

            var profiles = await GetProfilesAsync().ConfigureAwait(false);
            if (profiles.Count >= MaxProfiles)
            {
                return false;
            }

            var nextIndex = GetNextProfileIndex(profiles);
            if (!nextIndex.HasValue)
            {
                return false;
            }

            var created = NormalizeProfile(
                new UserProfile(
                    nextIndex.Value.ToString(),
                    nextIndex.Value,
                    trimmedName,
                    avatarColorHex,
                    false,
                    usesPrimaryAddons,
                    usesPrimaryPlugins,
                    avatarId,
                    avatarUrl),
                profiles.Count);

            var next = new List<UserProfile>(profiles) { created };
            await WriteProfilesAsync(next).ConfigureAwait(false);
            return true;
        }

        // JS parity: updateProfile ({...entry, ...profile} merge).
        public async Task<bool> UpdateProfileAsync(UserProfile profile)
        {
            if (profile == null)
            {
                return false;
            }

            var profiles = await GetProfilesAsync().ConfigureAwait(false);
            var next = new List<UserProfile>(profiles.Count);
            for (var i = 0; i < profiles.Count; i++)
            {
                var entry = profiles[i];
                if (entry.Id != profile.Id)
                {
                    next.Add(entry);
                    continue;
                }

                var mergedIndex = profile.ProfileIndex > 0 ? profile.ProfileIndex : entry.ProfileIndex;
                var merged = new UserProfile(
                    entry.Id,
                    mergedIndex,
                    profile.Name ?? entry.Name,
                    string.IsNullOrEmpty(profile.AvatarColorHex) ? entry.AvatarColorHex : profile.AvatarColorHex,
                    profile.IsPrimary || entry.IsPrimary,
                    profile.UsesPrimaryAddons || entry.UsesPrimaryAddons,
                    profile.UsesPrimaryPlugins || entry.UsesPrimaryPlugins,
                    profile.AvatarId ?? entry.AvatarId,
                    string.IsNullOrEmpty(profile.AvatarUrl) ? entry.AvatarUrl : profile.AvatarUrl);
                next.Add(NormalizeProfile(merged, i));
            }

            await WriteProfilesAsync(next).ConfigureAwait(false);
            return true;
        }

        // JS parity: deleteProfile — profile "1" is immutable; deleting the
        // active profile falls back to "1".
        public async Task<bool> DeleteProfileAsync(string id)
        {
            var normalizedId = id ?? "";
            if (normalizedId.Length == 0 || normalizedId == "1")
            {
                return false;
            }

            var profiles = await GetProfilesAsync().ConfigureAwait(false);
            var next = profiles.Where(profile => profile.Id != normalizedId).ToList();
            if (next.Count == profiles.Count)
            {
                return false;
            }

            await WriteProfilesAsync(next).ConfigureAwait(false);
            if (await GetActiveProfileId().ConfigureAwait(false) == normalizedId)
            {
                await LocalStore.SetAsync(ActiveProfileIdKey, "1", _store).ConfigureAwait(false);
            }

            return true;
        }

        // -------------------------------------------------------------------

        private async Task<List<JsonElement>> ReadStoredProfilesAsync()
        {
            try
            {
                var json = await _store.GetAsync(ProfilesKey).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json))
                {
                    return new List<JsonElement>();
                }

                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return new List<JsonElement>();
                    }

                    return document.RootElement.EnumerateArray().ToList();
                }
            }
            catch (JsonException)
            {
                return new List<JsonElement>();
            }
        }

        private Task WriteProfilesAsync(IReadOnlyList<UserProfile> profiles)
        {
            return LocalStore.SetAsync(ProfilesKey, profiles, _store);
        }

        private static string FirstNonEmpty(string left, string right)
        {
            return !string.IsNullOrEmpty(left) ? left : right;
        }

        private static string NonEmptyTrimmed(string value)
        {
            if (value == null)
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string Pick(string value, string fallback)
        {
            return value != null ? value : fallback;
        }
    }
}
