using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>Normalized custom plugin source (js PluginRuntime source shape).</summary>
    public sealed class PluginSource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string UrlTemplate { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Custom plugin-source sync. Behavioral spec (do not diverge):
    /// js/core/profile/pluginSyncService.js — plugins table select + sync_push_plugins RPC
    /// with legacy-table fallback.
    /// </summary>
    public sealed class PluginSyncService
    {
        private const string PluginsTable = "plugins";
        private const string PushRpc = "sync_push_plugins";
        private const string SourcesKey = "pluginSources";

        private static readonly Regex NonAlphanumeric = new Regex("[^a-z0-9]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly IProfileIdProvider _profileIds;
        private readonly SupabaseTableDelete _deleteAsync;
        private readonly Action<string> _logWarning;

        public PluginSyncService(
            SupabaseClient supabaseClient,
            AuthManager authManager,
            IKeyValueStore keyValueStore,
            IProfileIdProvider profileIdProvider = null,
            SupabaseTableDelete deleteAsync = null,
            Action<string> logWarning = null)
        {
            _supabase = supabaseClient ?? throw new ArgumentNullException(nameof(supabaseClient));
            _auth = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _profileIds = profileIdProvider ?? new DefaultProfileIdProvider();
            _deleteAsync = deleteAsync;
            _logWarning = logWarning;
        }

        // js resolveProfileId/resolvePluginProfileId. The usesPrimaryPlugins profiles lookup
        // needs ProfileManager and rides on the same seam — non-numeric active ids resolve
        // to profile 1.
        private long ResolveProfileId()
        {
            return SyncProfileIds.Parse(_profileIds.GetActiveProfileId());
        }

        // ------------------------------------------------------------------
        // Local plugin sources (js PluginRuntime.listSources/saveSources normalization)
        // ------------------------------------------------------------------

        public async Task<List<PluginSource>> ReadLocalSourcesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await LocalStore.GetAsync<List<JsonElement>>(
                SourcesKey, _store, null).ConfigureAwait(false);
            var sources = new List<PluginSource>();
            if (raw == null)
            {
                return sources;
            }
            foreach (var element in raw)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var id = OptionalString(element, "id");
                var name = (OptionalString(element, "name") ?? "Custom Source").Trim();
                if (string.IsNullOrEmpty(name))
                {
                    name = "Custom Source";
                }
                var urlTemplate = (OptionalString(element, "urlTemplate") ?? "").Trim();
                if (urlTemplate.Length == 0)
                {
                    continue;
                }
                sources.Add(new PluginSource
                {
                    Id = string.IsNullOrEmpty(id) ? GenerateSourceId() : id,
                    Name = name,
                    UrlTemplate = urlTemplate,
                    Enabled = !HasFalse(element, "enabled")
                });
            }
            return sources;
        }

        public async Task WriteLocalSourcesAsync(IReadOnlyList<PluginSource> sources)
        {
            var normalized = (sources ?? Array.Empty<PluginSource>())
                .Select(source => new Dictionary<string, object>
                {
                    ["id"] = source.Id,
                    ["name"] = source.Name,
                    ["urlTemplate"] = source.UrlTemplate,
                    ["enabled"] = source.Enabled
                })
                .ToList();
            await LocalStore.SetAsync(SourcesKey, normalized, _store).ConfigureAwait(false);
        }

        // js normalizeSources fallback id: plugin_<random base36>.
        internal static string GenerateSourceId()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            var suffix = new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
            return "plugin_" + suffix;
        }

        // js sourceIdFromUrl: last 18 alphanumerics of the URL, lowercased.
        internal static string SourceIdFromUrl(string url, int index)
        {
            var compact = NonAlphanumeric.Replace(url ?? "", "")
                .ToLowerInvariant();
            if (compact.Length > 18)
            {
                compact = compact.Substring(compact.Length - 18);
            }
            return "plugin_" + (index + 1) + "_" +
                   (compact.Length > 0 ? compact : "source");
        }

        // js mapRemoteRowsToSources.
        internal static List<PluginSource> MapRemoteRowsToSources(JsonElement rows)
        {
            var sources = new List<PluginSource>();
            if (rows.ValueKind != JsonValueKind.Array)
            {
                return sources;
            }
            var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var rowIndex = index;
                index++;
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var url = FirstString(row, "url", "url_template", "urlTemplate");
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }
                var name = OptionalString(row, "name");
                sources.Add(new PluginSource
                {
                    Id = SourceIdFromUrl(url, rowIndex),
                    Name = string.IsNullOrEmpty(name) ? "Plugin " + (rowIndex + 1) : name,
                    UrlTemplate = url,
                    Enabled = !HasFalse(row, "enabled")
                });
            }
            return sources;
        }

        // js mergeSources: remote order wins, remote fields override local, leftovers append.
        internal static List<PluginSource> MergeSources(
            IReadOnlyList<PluginSource> localSources, IReadOnlyList<PluginSource> remoteSources)
        {
            var local = localSources ?? Array.Empty<PluginSource>();
            var remote = remoteSources ?? Array.Empty<PluginSource>();
            if (remote.Count == 0)
            {
                return local.ToList();
            }
            var localByKey = new Dictionary<string, PluginSource>(StringComparer.Ordinal);
            foreach (var source in local)
            {
                var key = (source.UrlTemplate ?? "").Trim();
                if (key.Length == 0)
                {
                    continue;
                }
                localByKey[key] = source;
            }

            var merged = new List<PluginSource>();
            for (var index = 0; index < remote.Count; index++)
            {
                var remoteSource = remote[index];
                var key = (remoteSource.UrlTemplate ?? "").Trim();
                if (key.Length == 0)
                {
                    continue;
                }
                PluginSource localSource;
                localByKey.TryGetValue(key, out localSource);
                localByKey.Remove(key);
                merged.Add(new PluginSource
                {
                    Id = !string.IsNullOrEmpty(remoteSource.Id)
                        ? remoteSource.Id
                        : localSource != null && !string.IsNullOrEmpty(localSource.Id)
                            ? localSource.Id
                            : SourceIdFromUrl(key, index),
                    Name = remoteSource.Name,
                    UrlTemplate = remoteSource.UrlTemplate,
                    Enabled = remoteSource.Enabled
                });
            }
            foreach (var leftover in localByKey.Values)
            {
                merged.Add(leftover);
            }
            return merged;
        }

        // ------------------------------------------------------------------
        // Pull
        // ------------------------------------------------------------------

        public async Task<List<PluginSource>> PullAsync(CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return new List<PluginSource>();
                }
                var localSources = await ReadLocalSourcesAsync(ct).ConfigureAwait(false);
                var profileId = ResolveProfileId();
                var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                var rows = await _supabase.TableAsync<JsonElement>(
                    PluginsTable,
                    "user_id=eq." + Uri.EscapeDataString(ownerId) +
                    "&profile_id=eq." + profileId.ToString(CultureInfo.InvariantCulture) +
                    "&select=url,name,enabled,sort_order&order=sort_order.asc",
                    true, ct).ConfigureAwait(false);
                var remoteSources = MapRemoteRowsToSources(
                    JsonSerializer.SerializeToElement(rows));
                if (remoteSources.Count == 0 && localSources.Count > 0)
                {
                    return localSources;
                }
                var mergedSources = MergeSources(localSources, remoteSources);
                await WriteLocalSourcesAsync(mergedSources).ConfigureAwait(false);
                return mergedSources;
            }
            catch (Exception error)
            {
                Warn("Plugin sync pull failed", error);
                return new List<PluginSource>();
            }
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
                var profileId = ResolveProfileId();
                var sources = await ReadLocalSourcesAsync(ct).ConfigureAwait(false);
                try
                {
                    await _supabase.RpcAsync(PushRpc, BuildPushPayload(profileId, sources),
                        true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception rpcError)
                {
                    if (!SyncErrorClassifier.ShouldTryLegacyTable(rpcError))
                    {
                        throw;
                    }
                }

                var ownerId = await _auth.GetEffectiveUserIdAsync(ct).ConfigureAwait(false);
                var pid = profileId.ToString(CultureInfo.InvariantCulture);
                var rows = sources.Select((source, index) => (object)new Dictionary<string, object>
                {
                    ["user_id"] = ownerId,
                    ["profile_id"] = profileId,
                    ["url"] = source.UrlTemplate,
                    ["name"] = string.IsNullOrEmpty(source.Name)
                        ? "Plugin " + (index + 1)
                        : source.Name,
                    ["enabled"] = source.Enabled,
                    ["sort_order"] = index
                }).ToList();

                if (_deleteAsync != null)
                {
                    await _deleteAsync(
                        PluginsTable,
                        "user_id=eq." + Uri.EscapeDataString(ownerId) + "&profile_id=eq." + pid,
                        true, ct).ConfigureAwait(false);
                }
                if (rows.Count > 0)
                {
                    try
                    {
                        await _supabase.UpsertAsync(
                            PluginsTable, rows, "user_id,profile_id,url", true, ct)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // JS retries any upsert error without on_conflict.
                        await _supabase.UpsertAsync(PluginsTable, rows, null, true, ct)
                            .ConfigureAwait(false);
                    }
                }
                return true;
            }
            catch (Exception error)
            {
                Warn("Plugin sync push failed", error);
                return false;
            }
        }

        // js push payload: p_plugins in list order.
        internal static Dictionary<string, object> BuildPushPayload(
            long profileId, IReadOnlyList<PluginSource> sources)
        {
            var plugins = new List<object>();
            for (var index = 0; index < sources.Count; index++)
            {
                plugins.Add(new Dictionary<string, object>
                {
                    ["url"] = sources[index].UrlTemplate,
                    ["name"] = string.IsNullOrEmpty(sources[index].Name)
                        ? "Plugin " + (index + 1)
                        : sources[index].Name,
                    ["enabled"] = sources[index].Enabled,
                    ["sort_order"] = index
                });
            }
            return new Dictionary<string, object>
            {
                ["p_profile_id"] = profileId,
                ["p_plugins"] = plugins
            };
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

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

        private void Warn(string message, Exception error)
        {
            if (_logWarning != null)
            {
                _logWarning(message + ": " + (error != null ? error.Message : ""));
            }
        }
    }
}
