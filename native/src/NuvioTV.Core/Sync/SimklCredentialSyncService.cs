using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Simkl credential sync over the shared provider-credentials RPCs.
    /// Behavioral spec (do not diverge): js/core/profile/simklCredentialSyncService.js.
    /// </summary>
    public sealed class SimklCredentialSyncService
    {
        private const string Provider = "simkl";
        private const string PullRpc = "sync_pull_provider_credentials";
        private const string PushRpc = "sync_push_provider_credentials";
        private const string DeleteRpc = "sync_delete_provider_credentials";
        private const string AuthStoreKey = "simklAuthState";

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly IKeyValueStore _store;
        private readonly IProfileIdProvider _profileIds;
        private readonly ISyncClientIdProvider _clientIds;
        private readonly Action<string> _logWarning;

        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        public SimklCredentialSyncService(
            SupabaseClient supabaseClient,
            AuthManager authManager,
            IKeyValueStore keyValueStore,
            IProfileIdProvider profileIdProvider = null,
            ISyncClientIdProvider clientIdProvider = null,
            Action<string> logWarning = null)
        {
            _supabase = supabaseClient ?? throw new ArgumentNullException(nameof(supabaseClient));
            _auth = authManager ?? throw new ArgumentNullException(nameof(authManager));
            _store = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
            _profileIds = profileIdProvider ?? new DefaultProfileIdProvider();
            _clientIds = clientIdProvider != null
                ? clientIdProvider
                : new StoredSyncClientIdProvider(keyValueStore);
            _logWarning = logWarning;
        }

        // ------------------------------------------------------------------
        // Credential helpers (exported for tests/integration)
        // ------------------------------------------------------------------

        // js parseCredential: access_token required; snake/camel fallbacks; string or object.
        public static IReadOnlyDictionary<string, JsonElement> ParseCredential(JsonElement? value)
        {
            if (value == null)
            {
                return null;
            }
            var element = value.Value;
            if (element.ValueKind == JsonValueKind.String)
            {
                try
                {
                    element = JsonDocument.Parse(element.GetString()).RootElement.Clone();
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var accessToken = FirstString(element, "access_token", "accessToken").Trim();
            if (accessToken.Length == 0)
            {
                return null;
            }

            var username = FirstString(element, "username");
            long? accountId;
            if (!HasProperty(element, "account_id") && !HasProperty(element, "accountId"))
            {
                accountId = null;
            }
            else
            {
                var raw = FirstNumber(element, "account_id", "accountId");
                accountId = raw;
            }

            var state = new Dictionary<string, JsonElement>
            {
                ["accessToken"] = Text(accessToken),
                ["username"] = OptionalText(username),
                ["accountId"] = accountId.HasValue
                    ? JsonSerializer.SerializeToElement(accountId.Value)
                    : NullElement()
            };
            return state;
        }

        // ------------------------------------------------------------------
        // Operations
        // ------------------------------------------------------------------

        public Task<bool> PushCurrentToRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    var resolvedProfileId = SyncProfileIds.Resolve(profileId, _profileIds);
                    var state = await ReadStateAsync(resolvedProfileId).ConfigureAwait(false);
                    var accessToken = StringOrEmpty(state, "accessToken");
                    if (accessToken.Length == 0)
                    {
                        return false;
                    }
                    // js: { access_token } + optional username / account_id.
                    var credential = new Dictionary<string, object>
                    {
                        ["access_token"] = accessToken
                    };
                    var username = StringOrNull(state, "username");
                    if (!string.IsNullOrEmpty(username))
                    {
                        credential["username"] = username;
                    }
                    var accountId = NumberOrNull(state, "accountId");
                    if (accountId.HasValue)
                    {
                        credential["account_id"] = accountId.Value;
                    }
                    await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = resolvedProfileId,
                        ["p_origin_client_id"] =
                            await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false),
                        ["p_credentials"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["provider"] = Provider,
                                ["credential_json"] = credential
                            }
                        }
                    }, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Simkl credential sync push failed", error);
                    return false;
                }
            });
        }

        public Task<bool> PullFromRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    var resolvedProfileId = SyncProfileIds.Resolve(profileId, _profileIds);
                    var credentials = await _supabase.RpcAsync(PullRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = resolvedProfileId
                    }, true, ct).ConfigureAwait(false);

                    var row = FindProviderRow(credentials, Provider);
                    var remote = ParseCredential(UnwrapCredential(row));
                    if (remote == null)
                    {
                        return false;
                    }
                    var local = await ReadStateAsync(resolvedProfileId).ConfigureAwait(false);
                    if (StringOrNull(local, "accessToken") == StringOrNull(remote, "accessToken") &&
                        StringOrNull(local, "username") == StringOrNull(remote, "username") &&
                        NumberOrNull(local, "accountId") == NumberOrNull(remote, "accountId"))
                    {
                        return false;
                    }
                    await SaveTokenAsync(
                        resolvedProfileId, StringOrNull(remote, "accessToken")).ConfigureAwait(false);
                    await SaveIdentityAsync(resolvedProfileId, remote).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Simkl credential sync pull failed", error);
                    return false;
                }
            });
        }

        public Task<bool> DeleteRemoteAsync(
            long? profileId = null, CancellationToken ct = default)
        {
            return WithSyncLockAsync(async () =>
            {
                try
                {
                    if (!_auth.IsAuthenticated)
                    {
                        return false;
                    }
                    await _supabase.RpcAsync(DeleteRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = SyncProfileIds.Resolve(profileId, _profileIds),
                        ["p_origin_client_id"] =
                            await _clientIds.GetClientIdAsync(ct).ConfigureAwait(false),
                        ["p_provider"] = Provider
                    }, true, ct).ConfigureAwait(false);
                    return true;
                }
                catch (Exception error)
                {
                    Warn("Simkl credential sync delete failed", error);
                    return false;
                }
            });
        }

        // ------------------------------------------------------------------
        // simklAuthState envelope (js/data/local/simklAuthStore.js shape)
        // ------------------------------------------------------------------

        private Task<Dictionary<string, JsonElement>> ReadStateAsync(long profileId)
        {
            return RawProfileEnvelope.GetProfileAsync(_store, AuthStoreKey, Key(profileId));
        }

        // js SimklAuthStore.saveToken: token set, PIN session fields cleared, poll 5s.
        private async Task SaveTokenAsync(long profileId, string accessToken)
        {
            var state = new Dictionary<string, JsonElement>(
                await ReadStateAsync(profileId).ConfigureAwait(false));
            state["accessToken"] = Text(accessToken ?? "");
            state["userCode"] = NullElement();
            state["verificationUrl"] = NullElement();
            state["expiresAt"] = NullElement();
            state["pollInterval"] = JsonSerializer.SerializeToElement(5);
            await RawProfileEnvelope.SetProfileAsync(
                _store, AuthStoreKey, Key(profileId), state).ConfigureAwait(false);
        }

        // js SimklAuthStore.saveIdentity: username/accountId merged.
        private async Task SaveIdentityAsync(
            long profileId, IReadOnlyDictionary<string, JsonElement> remoteState)
        {
            var state = new Dictionary<string, JsonElement>(
                await ReadStateAsync(profileId).ConfigureAwait(false));
            state["username"] = OptionalText(StringOrNull(remoteState, "username"));
            var accountId = NumberOrNull(remoteState, "accountId");
            state["accountId"] = accountId.HasValue
                ? JsonSerializer.SerializeToElement(accountId.Value)
                : NullElement();
            await RawProfileEnvelope.SetProfileAsync(
                _store, AuthStoreKey, Key(profileId), state).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------

        internal static JsonElement? FindProviderRow(JsonElement rows, string provider)
        {
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Object &&
                        row.TryGetProperty("provider", out var rowProvider) &&
                        rowProvider.ValueKind == JsonValueKind.String &&
                        string.Equals(rowProvider.GetString(), provider,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return row.Clone();
                    }
                }
            }
            return null;
        }

        // js row.credential_json || row.credentialJson — strip the row wrapper first.
        internal static JsonElement? UnwrapCredential(JsonElement? row)
        {
            if (row == null || row.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            JsonElement value;
            if (!row.Value.TryGetProperty("credential_json", out value) &&
                !row.Value.TryGetProperty("credentialJson", out value))
            {
                return null;
            }
            return value.Clone();
        }

        private static bool HasProperty(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == name)
                {
                    return true;
                }
            }
            return false;
        }

        private static string FirstString(JsonElement element, params string[] keys)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return "";
            }
            foreach (var property in element.EnumerateObject())
            {
                if (keys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? "";
                }
            }
            return "";
        }

        private static long? FirstNumber(JsonElement element, params string[] keys)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (keys.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Number)
                {
                    long value;
                    if (property.Value.TryGetInt64(out value))
                    {
                        return value;
                    }
                    double parsed;
                    if (double.TryParse(property.Value.GetRawText(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out parsed) &&
                        !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                    {
                        return (long)Math.Truncate(parsed);
                    }
                    return null;
                }
            }
            return null;
        }

        private static string StringOrEmpty(IReadOnlyDictionary<string, JsonElement> state, string key)
        {
            var text = StringOrNull(state, key);
            return text ?? "";
        }

        private static string StringOrNull(IReadOnlyDictionary<string, JsonElement> state, string key)
        {
            JsonElement element;
            if (state == null || !state.TryGetValue(key, out element))
            {
                return null;
            }
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    return text.Length == 0 ? null : text;
                case JsonValueKind.Number:
                    return element.GetRawText();
                default:
                    return null;
            }
        }

        private static long? NumberOrNull(IReadOnlyDictionary<string, JsonElement> state, string key)
        {
            JsonElement element;
            if (state == null || !state.TryGetValue(key, out element) ||
                element.ValueKind != JsonValueKind.Number)
            {
                return null;
            }
            long value;
            if (element.TryGetInt64(out value))
            {
                return value;
            }
            double parsed;
            if (double.TryParse(element.GetRawText(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed) &&
                !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            {
                return (long)Math.Truncate(parsed);
            }
            return null;
        }

        private static JsonElement Text(string value)
        {
            return JsonSerializer.SerializeToElement(value ?? "");
        }

        private static JsonElement OptionalText(string value)
        {
            return string.IsNullOrEmpty(value) ? NullElement() : JsonSerializer.SerializeToElement(value);
        }

        private static JsonElement NullElement()
        {
            return JsonDocument.Parse("null").RootElement.Clone();
        }

        private static string Key(long profileId)
        {
            return profileId.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<T> WithSyncLockAsync<T>(Func<Task<T>> task)
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await task().ConfigureAwait(false);
            }
            finally
            {
                _syncLock.Release();
            }
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
