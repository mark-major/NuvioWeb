using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Models;
using NuvioTV.Core.Networking;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Remote profile list sync (pull/push) plus profile PIN and lock RPCs.
    ///
    /// Behavioral spec (do not diverge): js/core/profile/profileSyncService.js.
    /// RPC-first with table fallbacks for deployments that do not expose the
    /// sync functions: rpc sync_pull_profiles → table "profiles" (user_id) →
    /// legacy table "tv_profiles" (owner_id); push mirrors the same chain via
    /// delete+upsert / upsert.
    /// </summary>
    public sealed class ProfileSyncService
    {
        private const string Table = "tv_profiles";
        private const string FallbackTable = "profiles";
        private const string PullRpc = "sync_pull_profiles";
        private const string PushRpc = "sync_push_profiles";
        private const string PullLocksRpc = "sync_pull_profile_locks";
        private const string SetProfilePinRpc = "set_profile_pin";
        private const string ClearProfilePinRpc = "clear_profile_pin";
        private const string VerifyProfilePinRpc = "verify_profile_pin";
        private const string DeleteProfileDataRpc = "sync_delete_profile_data";

        private static readonly object EmptyPayload = new { };
        private static readonly IReadOnlyList<UserProfile> EmptyProfiles = new UserProfile[0];

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly ProfileManager _profiles;

        public ProfileSyncService(SupabaseClient supabase, AuthManager auth, ProfileManager profiles)
        {
            _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        }

        // -------------------------------------------------------------------
        // Error classification (profileSyncService.js:15-41)
        // -------------------------------------------------------------------

        // JS parity: shouldTryLegacyTable — 404 or PGRST205 / missing-table text.
        public static bool ShouldTryLegacyTable(Exception error)
        {
            if (error == null)
            {
                return false;
            }

            var http = error as NuvioHttpException;
            if (http != null && http.Status == 404)
            {
                return true;
            }

            if (http != null && http.Code == "PGRST205")
            {
                return true;
            }

            var message = error.Message ?? "";
            return message.Contains("PGRST205") || message.Contains("Could not find the table");
        }

        // JS parity: shouldTryProfileTableFallback — 404 or PGRST202 /
        // missing-function text across message + detail.
        public static bool ShouldTryProfileTableFallback(NuvioHttpException error)
        {
            if (error == null)
            {
                return false;
            }

            if (error.Status == 404)
            {
                return true;
            }

            if (error.Code == "PGRST202")
            {
                return true;
            }

            var message = (error.Message ?? "") + " " + (error.Detail ?? "");
            return message.Contains("PGRST202") || message.Contains("Could not find the function");
        }

        // -------------------------------------------------------------------
        // Pull (profileSyncService.js:67-109)
        // -------------------------------------------------------------------

        public async Task<IReadOnlyList<UserProfile>> PullAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return EmptyProfiles;
                }

                IReadOnlyList<JsonElement> rows;
                try
                {
                    var rpcRows = await _supabase.RpcAsync(PullRpc, EmptyPayload, true, ct).ConfigureAwait(false);
                    rows = rpcRows.ValueKind == JsonValueKind.Array
                        ? rpcRows.EnumerateArray().ToList()
                        : new List<JsonElement>();
                }
                catch (NuvioHttpException rpcError)
                {
                    // A table fallback is useful only for deployments that do not
                    // expose the profiles RPC; other failures propagate untouched so
                    // boot is not held past its watchdog.
                    if (!ShouldTryProfileTableFallback(rpcError))
                    {
                        throw;
                    }

                    var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                    try
                    {
                        rows = await SelectOrderedRowsAsync(FallbackTable, "user_id", ownerId, ct).ConfigureAwait(false);
                    }
                    catch (Exception primaryError)
                    {
                        if (!ShouldTryLegacyTable(primaryError))
                        {
                            throw rpcError;
                        }

                        rows = await SelectOrderedRowsAsync(Table, "owner_id", ownerId, ct).ConfigureAwait(false);
                    }
                }

                var profiles = MapRows(rows);
                if (profiles.Count > 0)
                {
                    await _profiles.ReplaceProfilesAsync(profiles).ConfigureAwait(false);
                }

                return profiles;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                // JS parity: console.warn("Profile sync pull failed") → empty result.
                return EmptyProfiles;
            }
        }

        private Task<IReadOnlyList<JsonElement>> SelectOrderedRowsAsync(string table, string ownerColumn, string ownerId, CancellationToken ct)
        {
            return _supabase.TableAsync<JsonElement>(
                table, BuildOrderedQuery(ownerColumn, ownerId), true, ct);
        }
        public static IReadOnlyList<UserProfile> MapRows(IReadOnlyList<JsonElement> rows)
        {
            var mapped = new List<UserProfile>();
            foreach (var row in rows)
            {
                mapped.Add(MapProfileRow(row));
            }

            return mapped;
        }
        private static string BuildOrderedQuery(string ownerColumn, string ownerId)
        {
            return ownerColumn + "=eq." + Uri.EscapeDataString(ownerId ?? "") +
                "&select=*&order=profile_index.asc";
        }


        public async Task<bool> PushAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                var profiles = await _profiles.GetProfilesAsync().ConfigureAwait(false);

                try
                {
                    var payload = new
                    {
                        p_client_max_profiles = ProfileManager.MaxProfiles,
                        p_profiles = profiles.Select(BuildRpcProfile).ToArray()
                    };
                    await _supabase.RpcAsync(PushRpc, payload, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (NuvioHttpException)
                {
                    // JS parity: console.warn then fall back to table sync on any
                    // RPC failure (profileSyncService.js:140-142).
                }

                var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                var rows = profiles.Select(profile => BuildTableRow(ownerId, profile)).ToArray();
                var fallbackRows = rows.Select(row => new Dictionary<string, object>
                {
                    ["user_id"] = ownerId,
                    ["profile_index"] = row["profile_index"],
                    ["name"] = row["name"],
                    ["avatar_color_hex"] = row["avatar_color_hex"],
                    ["avatar_id"] = row["avatar_id"] ?? null,
                    ["avatar_url"] = row["avatar_url"] ?? null,
                    ["uses_primary_addons"] = (bool)row["uses_primary_addons"],
                    ["uses_primary_plugins"] = (bool)row["uses_primary_plugins"]
                }).ToArray();

                try
                {
                    await _supabase.DeleteAsync(FallbackTable, "user_id=eq." + Uri.EscapeDataString(ownerId ?? ""), true, ct).ConfigureAwait(false);
                    if (fallbackRows.Length > 0)
                    {
                        await _supabase.UpsertAsync(FallbackTable, fallbackRows, "user_id,profile_index", true, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception primaryError)
                {
                    if (!ShouldTryLegacyTable(primaryError))
                    {
                        throw;
                    }

                    await _supabase.UpsertAsync(Table, rows, "id", true, ct).ConfigureAwait(false);
                }

                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                // JS parity: console.warn("Profile sync push failed") → false.
                return false;
            }
        }

        // JS parity push payload entry (profileSyncService.js:122-135).
        private static object BuildRpcProfile(UserProfile profile)
        {
            var profileIndex = ResolveProfileIndex(profile);
            var avatarUrl = NonEmptyTrimmed(profile.AvatarUrl);
            return new Dictionary<string, object>
            {
                ["profile_index"] = profileIndex,
                ["name"] = profile.Name,
                ["avatar_color_hex"] = string.IsNullOrEmpty(profile.AvatarColorHex)
                    ? "#1E88E5"
                    : profile.AvatarColorHex,
                ["avatar_id"] = avatarUrl != null ? null : profile.AvatarId,
                ["avatar_url"] = avatarUrl,
                ["uses_primary_addons"] = profile.UsesPrimaryAddons,
                ["uses_primary_plugins"] = profile.UsesPrimaryPlugins
            };
        }

        // JS parity legacy-table row (profileSyncService.js:145-161).
        private static Dictionary<string, object> BuildTableRow(string ownerId, UserProfile profile)
        {
            var profileIndex = ResolveProfileIndex(profile);
            var avatarUrl = NonEmptyTrimmed(profile.AvatarUrl);
            return new Dictionary<string, object>
            {
                ["id"] = profile.Id,
                ["owner_id"] = ownerId,
                ["profile_index"] = profileIndex,
                ["name"] = profile.Name,
                ["avatar_color_hex"] = string.IsNullOrEmpty(profile.AvatarColorHex)
                    ? "#1E88E5"
                    : profile.AvatarColorHex,
                ["avatar_id"] = avatarUrl != null ? null : profile.AvatarId,
                ["avatar_url"] = avatarUrl,
                ["uses_primary_addons"] = profile.UsesPrimaryAddons,
                ["uses_primary_plugins"] = profile.UsesPrimaryPlugins,
                ["is_primary"] = profile.IsPrimary
            };
        }

        private static int ResolveProfileIndex(UserProfile profile)
        {
            return profile.ProfileIndex > 0
                ? profile.ProfileIndex
                : int.TryParse(profile.Id == null ? "" : profile.Id.Trim(), out var parsed) && parsed > 0
                    ? parsed
                    : 1;
        }


        public static UserProfile MapProfileRow(JsonElement row)
        {
            var rawIndex = SyncJson.GetNumber(row, "profile_index", "profileIndex", "id");
            var normalizedIndex = rawIndex.HasValue && rawIndex.Value > 0
                ? (int)Math.Truncate(rawIndex.Value)
                : 1;

            return new UserProfile(
                normalizedIndex.ToString(),
                normalizedIndex,
                SyncJson.GetStringOrNull(row, "name") ?? ("Profile " + normalizedIndex),
                SyncJson.GetStringOrNull(row, "avatar_color_hex", "avatarColorHex") ?? "#1E88E5",
                SyncJson.GetBool(row, "is_primary").GetValueOrDefault(normalizedIndex == 1),
                SyncJson.GetBool(row, "uses_primary_addons")
                    ?? SyncJson.GetBool(row, "usesPrimaryAddons").GetValueOrDefault(false),
                SyncJson.GetBool(row, "uses_primary_plugins")
                    ?? SyncJson.GetBool(row, "usesPrimaryPlugins").GetValueOrDefault(false),
                FirstNonEmpty(SyncJson.GetStringOrNull(row, "avatar_id", "avatarId"), null),
                FirstNonEmpty(SyncJson.GetStringOrNull(row, "avatar_url", "avatarUrl"), null));
        }

        // -------------------------------------------------------------------
        // Lock states + PIN RPCs (profileSyncService.js:191-295)
        // -------------------------------------------------------------------

        // JS parity: pullProfileLockStates — map of "profileIndex" → pinEnabled.
        public async Task<IReadOnlyDictionary<string, bool>> PullProfileLockStatesAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return EmptyLockStates;
                }

                var rows = await _supabase.RpcAsync(PullLocksRpc, EmptyPayload, true, ct).ConfigureAwait(false);
                var states = new Dictionary<string, bool>();
                if (rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var index = SyncJson.GetNumber(row, "profile_index", "profileIndex", "id");
                        if (!index.HasValue || index.Value <= 0)
                        {
                            continue;
                        }

                        states[Math.Truncate(index.Value).ToString()] =
                            SyncJson.GetBool(row, "pin_enabled", "pinEnabled").GetValueOrDefault(false);
                    }
                }

                return states;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return EmptyLockStates;
            }
        }

        // JS parity: setProfilePin.
        public async Task<bool> SetProfilePinAsync(string profileId, string pin, string currentPin = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                var parameters = new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToProfileNumber(profileId),
                    ["p_pin"] = pin ?? ""
                };
                AddCurrentPin(parameters, "p_current_pin", currentPin);
                await _supabase.RpcAsync(SetProfilePinRpc, parameters, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        // JS parity: clearProfilePin.
        public async Task<bool> ClearProfilePinAsync(string profileId, string currentPin = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                var parameters = new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToProfileNumber(profileId)
                };
                AddCurrentPin(parameters, "p_current_pin", currentPin);
                await _supabase.RpcAsync(ClearProfilePinRpc, parameters, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        // JS parity: verifyProfilePin — unwraps a single-element array response.
        public async Task<ProfilePinVerifyResult> VerifyProfilePinAsync(string profileId, string pin, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return null;
                }

                var payload = await _supabase.RpcAsync(VerifyProfilePinRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToProfileNumber(profileId),
                    ["p_pin"] = pin ?? ""
                }, true, ct).ConfigureAwait(false);

                var response = payload.ValueKind == JsonValueKind.Array
                    && payload.GetArrayLength() > 0
                        ? payload.EnumerateArray().First()
                        : payload;

                var unlocked = response.ValueKind == JsonValueKind.Object &&
                    response.TryGetProperty("unlocked", out var unlockedElem) &&
                    unlockedElem.ValueKind == JsonValueKind.True;
                var retryAfter = Math.Max(
                    0,
                    (long)(response.ValueKind == JsonValueKind.Object
                        ? SyncJson.GetNumber(response, "retry_after_seconds", "retryAfterSeconds").GetValueOrDefault(0)
                        : 0));

                return new ProfilePinVerifyResult(unlocked, retryAfter);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return null;
            }
        }

        // JS parity: deleteProfileData.
        public async Task<bool> DeleteProfileDataAsync(string profileId, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                await _supabase.RpcAsync(DeleteProfileDataRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ToProfileNumber(profileId)
                }, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        // -------------------------------------------------------------------

        private static void AddCurrentPin(Dictionary<string, object> parameters, string key, string currentPin)
        {
            var trimmed = (currentPin ?? "").Trim();
            if (trimmed.Length > 0)
            {
                parameters[key] = trimmed;
            }
        }

        // JS parity: Number(profileId); unparsable values coerce to 0.
        private static long ToProfileNumber(string profileId)
        {
            return long.TryParse((profileId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
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

        private static readonly IReadOnlyDictionary<string, bool> EmptyLockStates =
            new Dictionary<string, bool>();
    }

    /// <summary>Result of verify_profile_pin (JS anonymous object).</summary>
    public sealed class ProfilePinVerifyResult
    {
        public bool Unlocked { get; }
        public long RetryAfterSeconds { get; }

        public ProfilePinVerifyResult(bool unlocked, long retryAfterSeconds)
        {
            Unlocked = unlocked;
            RetryAfterSeconds = retryAfterSeconds;
        }
    }

    /// <summary>
    /// Shared lenient JSON accessors mirroring JavaScript coercion semantics
    /// (Number()/String()/Boolean() over loosely typed remote rows).
    /// </summary>
    internal static class SyncJson
    {
        public static JsonElement GetProperty(JsonElement obj, string name)
        {
            if (obj.ValueKind == JsonValueKind.Object && name != null && obj.TryGetProperty(name, out var value))
            {
                return value;
            }

            return default;
        }

        public static bool HasProperty(JsonElement obj, string name)
        {
            return obj.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(name) && obj.TryGetProperty(name, out _);
        }

        // Returns the first property whose JSON value is a non-empty string
        // (JSON null/undefined/other kinds are skipped).
        public static string GetStringOrNull(JsonElement obj, params string[] names)
        {
            foreach (var name in names)
            {
                if (obj.ValueKind != JsonValueKind.Object)
                {
                    break;
                }

                if (!obj.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }
            }

            return null;
        }

        // JS Number(value): numeric JSON values pass through; strings are parsed;
        // anything else (incl. null/objects) yields null (NaN).
        public static double? CoerceNumber(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    return value.GetDouble();
                case JsonValueKind.String:
                    double parsed;
                    if (double.TryParse(value.GetString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }

                    return null;
                default:
                    return null;
            }
        }

        // Returns the first named property coercible to a finite number.
        public static double? GetNumber(JsonElement obj, params string[] names)
        {
            foreach (var name in names)
            {
                var value = CoerceNumber(GetProperty(obj, name));
                if (value.HasValue)
                {
                    return value;
                }
            }

            return null;
        }

        // Strict boolean read: only real JSON booleans count (matches the JS
        // `typeof x === "boolean"` guards in the sync services).
        public static bool? GetBool(JsonElement obj, params string[] names)
        {
            if (obj.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (!obj.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                {
                    return value.GetBoolean();
                }
            }

            return null;
        }
    }
}
