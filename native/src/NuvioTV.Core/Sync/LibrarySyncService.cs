using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Addons;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Installed-addons library sync. Behavioral spec (do not diverge):
    /// js/core/profile/librarySyncService.js — addons table first, tv_addons fallback,
    /// sync_pull_addons/sync_push_addons RPCs, legacy-table push fallback.
    /// </summary>
    public sealed class LibrarySyncService
    {
        private const string AddonsTable = "addons";
        private const string TvAddonsTable = "tv_addons";

        // Keys mirror AddonRepository's private constants; writes go through the same
        // ProfileScopedStore envelope format so both readers stay compatible.
        private const string AddonDisplayNamesKey = "installedAddonDisplayNames";
        private const string AddonEnabledStatesKey = "installedAddonEnabledStates";

        public sealed class PullStatus
        {
            [JsonPropertyName("state")]
            public string State { get; set; }

            [JsonPropertyName("count")]
            public long Count { get; set; }

            [JsonPropertyName("error")]
            public string Error { get; set; }

            [JsonPropertyName("at")]
            public long AtMs { get; set; }
        }

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly AddonRepository _addonRepository;
        private readonly IProfileIdProvider _profileIds;
        private readonly SupabaseTableDelete _deleteAsync;
        private readonly Action<string> _logWarning;
        private readonly Func<DateTimeOffset> _utcNow;

        private PullStatus _lastPullStatus = new PullStatus
        {
            State = "idle",
            Count = 0,
            Error = null,
            AtMs = 0
        };

        public LibrarySyncService(
            SupabaseClient supabaseClient,
            AuthManager authManager,
            AddonRepository addonRepository,
            IKeyValueStore keyValueStore,
            IProfileIdProvider profileIdProvider = null,
            SupabaseTableDelete deleteAsync = null,
            Func<DateTimeOffset> utcNow = null,
            Action<string> logWarning = null)
        {
            _supabase = supabaseClient ?? throw new ArgumentNullException(nameof(supabaseClient));
            _auth = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _addonRepository = addonRepository ??
                throw new ArgumentNullException(nameof(addonRepository));
            _profileIds = profileIdProvider ?? new DefaultProfileIdProvider();
            _deleteAsync = deleteAsync;
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _logWarning = logWarning;
        }

        public PullStatus GetLastPullStatus()
        {
            lock (_statusGate)
            {
                return new PullStatus
                {
                    State = _lastPullStatus.State,
                    Count = _lastPullStatus.Count,
                    Error = _lastPullStatus.Error,
                    AtMs = _lastPullStatus.AtMs
                };
            }
        }

        // js resolveProfileId/resolveAddonProfileId: numeric active id, profile 1 default.
        // The usesPrimaryAddons profiles lookup needs ProfileManager and rides on the same
        // seam — non-numeric active ids resolve to profile 1.
        private long ResolveProfileId()
        {
            return SyncProfileIds.Parse(_profileIds.GetActiveProfileId());
        }

        // ------------------------------------------------------------------
        // Row mapping (js extractAddonEntries / applyPulledAddons)
        // ------------------------------------------------------------------

        internal static IReadOnlyList<AddonEntry> ExtractAddonEntries(JsonElement rows)
        {
            var entries = new List<AddonEntry>();
            if (rows.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var url = FirstString(row, "url", "base_url");
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }
                var displayName = FirstString(
                    row, "display_name", "displayName", "custom_name", "customName",
                    "alias", "name");
                var name = OptionalString(row, "name");
                bool enabled = !HasFalse(row, "enabled");
                entries.Add(new AddonEntry
                {
                    Url = url,
                    DisplayName = displayName.Length > 0 ? displayName : null,
                    Name = name,
                    Enabled = enabled
                });
            }
            return entries;
        }

        internal sealed class AddonEntry
        {
            public string Url { get; set; }
            public string DisplayName { get; set; }
            public string Name { get; set; }
            public bool Enabled { get; set; }
        }

        private static string FirstString(JsonElement row, params string[] keys)
        {
            foreach (var key in keys)
            {
                JsonElement element;
                if (row.TryGetProperty(key, out element) &&
                    element.ValueKind == JsonValueKind.String)
                {
                    var text = element.GetString() ?? "";
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
            return "";
        }

        private static string OptionalString(JsonElement row, string key)
        {
            JsonElement element;
            if (row.TryGetProperty(key, out element) && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? "";
            }
            return null;
        }

        private static bool HasFalse(JsonElement row, string key)
        {
            JsonElement element;
            return row.TryGetProperty(key, out element) &&
                   element.ValueKind == JsonValueKind.False;
        }

        // js applyPulledAddons: replace display-name overrides + enabled states, then order.
        private async Task<IReadOnlyList<string>> ApplyPulledAddonsAsync(
            JsonElement rows, long profileId, CancellationToken ct)
        {
            var entries = ExtractAddonEntries(rows);
            var pid = Key(profileId);

            // setAddonDisplayNameOverrides(entries, { replace: true })
            var currentOverrides =
                await DisplayNamesStore().GetAsync(pid).ConfigureAwait(false);
            var nextOverrides = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(entry.Url);
                if (string.IsNullOrEmpty(cleanUrl))
                {
                    continue;
                }
                var name = entry.DisplayName ?? entry.Name;
                if (string.IsNullOrEmpty(name) &&
                    !currentOverrides.TryGetValue(cleanUrl, out name))
                {
                    name = "";
                }
                if (!string.IsNullOrEmpty(name))
                {
                    nextOverrides[cleanUrl] = name.Trim();
                }
            }
            await DisplayNamesStore().ReplaceAsync(pid, nextOverrides).ConfigureAwait(false);

            // setAddonEnabledStates(entries, { replace: true })
            var nextStates = new Dictionary<string, bool>();
            foreach (var entry in entries)
            {
                var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                    AddonUrlBuilder.CanonicalizeUrl(entry.Url));
                if (!string.IsNullOrEmpty(cleanUrl))
                {
                    nextStates[cleanUrl] = entry.Enabled;
                }
            }
            await EnabledStatesStore().ReplaceAsync(pid, nextStates).ConfigureAwait(false);

            var urls = entries.Select(entry => entry.Url).ToList();
            await _addonRepository.SetAddonOrderAsync(urls, silent: true, profileId: pid)
                .ConfigureAwait(false);
            return urls;
        }

        private ProfileScopedStore<Dictionary<string, string>> DisplayNamesStore()
        {
            return ProfileScopedStore<Dictionary<string, string>>.CreateWithStore(
                AddonDisplayNamesKey,
                _store,
                normalize: NormalizeDisplayNameOverrides,
                merge: null);
        }

        private ProfileScopedStore<Dictionary<string, bool>> EnabledStatesStore()
        {
            return ProfileScopedStore<Dictionary<string, bool>>.CreateWithStore(
                AddonEnabledStatesKey,
                _store,
                normalize: NormalizeEnabledStates,
                merge: null);
        }

        // Replicates AddonRepository's normalizers for envelope compatibility.
        private static Dictionary<string, string> NormalizeDisplayNameOverrides(
            Dictionary<string, string> value)
        {
            var result = new Dictionary<string, string>();
            if (value == null)
            {
                return result;
            }
            foreach (var entry in value)
            {
                var cleanUrl = AddonUrlBuilder.CanonicalizeUrl(entry.Key);
                var cleanName = (entry.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(cleanUrl) && !string.IsNullOrEmpty(cleanName))
                {
                    result[cleanUrl] = cleanName;
                }
            }
            return result;
        }

        private static Dictionary<string, bool> NormalizeEnabledStates(
            Dictionary<string, bool> value)
        {
            var result = new Dictionary<string, bool>();
            if (value == null)
            {
                return result;
            }
            foreach (var entry in value)
            {
                var cleanUrl = AddonCanonicalizer.NormalizeCinemetaUrl(
                    AddonUrlBuilder.CanonicalizeUrl(entry.Key));
                if (!string.IsNullOrEmpty(cleanUrl))
                {
                    result[cleanUrl] = entry.Value;
                }
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Pull
        // ------------------------------------------------------------------
        public async Task<IReadOnlyList<string>> PullAsync(CancellationToken ct = default)
        {
            Exception readError = null;
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    RecordPullStatus("signed-out", 0, null);
                    return new string[0];
                }
                var pid = Key(ResolveProfileId());
                var localUrls =
                    await _addonRepository.GetInstalledAddonUrlsAsync(pid).ConfigureAwait(false);
                var profileId = ResolveProfileId();
                // JS resolves the effective owner once, outside the per-table fallbacks.
                var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                var addonTableMissing = false;
                var tvTableMissing = false;
                try
                {
                    var addonRows = await _supabase.TableAsync<JsonElement>(
                        AddonsTable,
                        "user_id=eq." + Uri.EscapeDataString(ownerId) +
                        "&profile_id=eq." + pid + "&select=*&order=sort_order.asc",
                        true, ct).ConfigureAwait(false);
                    var urls = await ApplyPulledAddonsAsync(
                        RowsElement(addonRows), profileId, ct).ConfigureAwait(false);
                    RecordPullStatus("ok", urls.Count, null);
                    return urls;
                }
                catch (Exception addonsTableError)
                {
                    addonTableMissing = SyncErrorClassifier.IsMissingResourceError(addonsTableError);
                    if (!addonTableMissing)
                    {
                        readError = addonsTableError;
                    }
                    Warn("Addon sync pull addons-table read failed", addonsTableError);
                }
                try
                {
                    var rows = await _supabase.TableAsync<JsonElement>(
                        TvAddonsTable,
                        "owner_id=eq." + Uri.EscapeDataString(ownerId) +
                        "&select=*&order=position.asc",
                        true, ct).ConfigureAwait(false);
                    var urls = await ApplyPulledAddonsAsync(
                        RowsElement(rows), profileId, ct).ConfigureAwait(false);
                    RecordPullStatus("ok", urls.Count, null);
                    return urls;
                }
                catch (Exception tvTableError)
                {
                    tvTableMissing = SyncErrorClassifier.IsMissingResourceError(tvTableError);
                    if (!tvTableMissing)
                    {
                        readError = tvTableError;
                    }
                    Warn("Addon sync pull tv-table read failed", tvTableError);
                }

                if (addonTableMissing && tvTableMissing)
                {
                    try
                    {
                        var rpcRows = await _supabase.RpcAsync(
                            "sync_pull_addons",
                            new Dictionary<string, object> { ["p_profile_id"] = profileId },
                            true, ct).ConfigureAwait(false);
                        var urls = await ApplyPulledAddonsAsync(
                            rpcRows, profileId, ct).ConfigureAwait(false);
                        RecordPullStatus("ok", urls.Count, null);
                        return urls;
                    }
                    catch (Exception rpcError)
                    {
                        readError = rpcError;
                        Warn("Addon sync pull RPC failed", rpcError);
                    }
                }

                if (readError != null)
                {
                    RecordPullStatus("error", localUrls.Count, readError.Message);
                }
                else
                {
                    RecordPullStatus("ok", localUrls.Count, null);
                }
                return localUrls.Count > 0 ? localUrls : new List<string>();
            }
            catch (Exception error)
            {
                RecordPullStatus("error", 0, error.Message);
                Warn("Library sync pull failed", error);
                return new List<string>();
            }
        }

        private static JsonElement RowsElement(IReadOnlyList<JsonElement> rows)
        {
            // TableAsync already returns an array; re-wrap so row mapping shares one path.
            return JsonSerializer.SerializeToElement(rows);
        }

        // ------------------------------------------------------------------
        // Push
        // ------------------------------------------------------------------

        public async Task<bool> PushAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }
                var pid = Key(ResolveProfileId());
                var profileId = ResolveProfileId();
                var urls =
                    await _addonRepository.GetInstalledAddonUrlsAsync(pid).ConfigureAwait(false);

                try
                {
                    var addons = new List<object>();
                    for (var index = 0; index < urls.Count; index++)
                    {
                        var url = urls[index];
                        var addon = new Dictionary<string, object>
                        {
                            ["url"] = url,
                            ["sort_order"] = index,
                            ["enabled"] =
                                await _addonRepository.IsAddonEnabledAsync(url, pid)
                                    .ConfigureAwait(false)
                        };
                        var overrideName =
                            await _addonRepository.GetAddonDisplayNameOverrideAsync(url, pid)
                                .ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(overrideName))
                        {
                            addon["name"] = overrideName;
                        }
                        addons.Add(addon);
                    }
                    await _supabase.RpcAsync("sync_push_addons", new Dictionary<string, object>
                    {
                        ["p_profile_id"] = profileId,
                        ["p_addons"] = addons
                    }, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception rpcError)
                {
                    Warn("Addon sync push RPC failed, falling back to legacy table", rpcError);
                }

                var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_deleteAsync != null)
                    {
                        await _deleteAsync(
                            AddonsTable,
                            "user_id=eq." + Uri.EscapeDataString(ownerId) +
                            "&profile_id=eq." + pid,
                            true, ct).ConfigureAwait(false);
                    }
                    var addonRows = new List<object>();
                    for (var index = 0; index < urls.Count; index++)
                    {
                        var url = urls[index];
                        var name =
                            await _addonRepository.GetAddonDisplayNameOverrideAsync(url, pid)
                                .ConfigureAwait(false);
                        var row = new Dictionary<string, object>
                        {
                            ["user_id"] = ownerId,
                            ["profile_id"] = profileId,
                            ["url"] = url,
                            ["sort_order"] = index,
                            ["enabled"] =
                                await _addonRepository.IsAddonEnabledAsync(url, pid)
                                    .ConfigureAwait(false)
                        };
                        if (!string.IsNullOrEmpty(name))
                        {
                            row["name"] = name;
                        }
                        addonRows.Add(row);
                    }
                    if (addonRows.Count > 0)
                    {
                        try
                        {
                            await _supabase.UpsertAsync(
                                AddonsTable, addonRows, "user_id,profile_id,url", true, ct)
                                .ConfigureAwait(false);
                        }
                        catch (Exception upsertError)
                        {
                            if (!SyncErrorClassifier.IsOnConflictConstraintError(upsertError))
                            {
                                throw;
                            }
                            await _supabase.UpsertAsync(
                                AddonsTable, addonRows, null, true, ct).ConfigureAwait(false);
                        }
                    }
                    return true;
                }
                catch (Exception addonsTableError)
                {
                    if (!SyncErrorClassifier.IsMissingResourceError(addonsTableError))
                    {
                        Warn("Addon sync push addons-table fallback failed", addonsTableError);
                        return false;
                    }
                    Warn(
                        "Addon sync push addons-table missing, trying tv_addons fallback",
                        addonsTableError);
                }

                var legacyRows = new List<object>();
                for (var index = 0; index < urls.Count; index++)
                {
                    legacyRows.Add(new Dictionary<string, object>
                    {
                        ["owner_id"] = ownerId,
                        ["base_url"] = urls[index],
                        ["position"] = index
                    });
                }
                try
                {
                    if (_deleteAsync != null)
                    {
                        await _deleteAsync(
                            TvAddonsTable,
                            "owner_id=eq." + Uri.EscapeDataString(ownerId),
                            true, ct).ConfigureAwait(false);
                    }
                    if (legacyRows.Count > 0)
                    {
                        await _supabase.UpsertAsync(
                            TvAddonsTable, legacyRows, "owner_id,base_url", true, ct)
                            .ConfigureAwait(false);
                    }
                    return true;
                }
                catch (Exception tvTableError)
                {
                    Warn("Addon sync push tv_addons fallback failed", tvTableError);
                    return false;
                }
            }
            catch (Exception error)
            {
                Warn("Library sync push failed", error);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Status + misc
        // ------------------------------------------------------------------

        private void RecordPullStatus(string state, long count, string error)
        {
            lock (_statusGate)
            {
                _lastPullStatus = new PullStatus
                {
                    State = state,
                    Count = count,
                    Error = error,
                    AtMs = _utcNow().ToUnixTimeSeconds() * 1000
                };
            }
        }

        private readonly object _statusGate = new object();

        private static string Key(long profileId)
        {
            return profileId.ToString(CultureInfo.InvariantCulture);
        }

        private void Warn(string message, Exception error)
        {
            if (_logWarning != null)
            {
                _logWarning(message + ": " + (error != null ? error.Message : ""));
            }
        }
    }
}
