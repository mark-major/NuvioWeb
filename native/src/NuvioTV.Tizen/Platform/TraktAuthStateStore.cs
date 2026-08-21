using System;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Integrations.Trakt;
using NuvioTV.Core.Storage;

namespace NuvioTV.Tizen.Platform
{
    /// <summary>
    /// ITraktAuthStateStore backed by JsonFileStore. Keys follow Appendix B
    /// (traktAuthState per profile; single-profile device flow scratch space).
    /// </summary>
    public sealed class TraktAuthStateStore : ITraktAuthStateStore
    {
        private const string TokenKeyPrefix = "traktAuthState:";
        private const string DeviceFlowKey = "traktDeviceFlow";
        private const string PollIntervalKey = "traktPollIntervalSeconds";

        private readonly IKeyValueStore _store;
        private readonly Func<string> _profileIdProvider;

        public TraktAuthStateStore(IKeyValueStore store, Func<string> profileIdProvider = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _profileIdProvider = profileIdProvider ?? (() => "1");
        }

        public int PollIntervalSeconds { get; set; } = 5;

        public TraktTokenResponse GetToken()
        {
            var raw = _store.GetAsync(TokenKeyPrefix + _profileIdProvider()).GetAwaiter().GetResult();
            return string.IsNullOrEmpty(raw)
                ? null
                : JsonSerializer.Deserialize<TraktTokenResponse>(raw);
        }

        public void SaveToken(TraktTokenResponse token)
        {
            var raw = JsonSerializer.Serialize(token);
            _store.SetAsync(TokenKeyPrefix + _profileIdProvider(), raw).Wait();
        }

        public void ClearAuth()
        {
            _store.RemoveAsync(TokenKeyPrefix + _profileIdProvider()).Wait();
        }

        public void SaveDeviceFlow(TraktDeviceCode flow)
        {
            _store.SetAsync(DeviceFlowKey, JsonSerializer.Serialize(flow)).Wait();
            _store.SetAsync(PollIntervalKey, PollIntervalSeconds.ToString()).Wait();
        }

        public TraktDeviceCode GetDeviceFlow()
        {
            var raw = _store.GetAsync(DeviceFlowKey).GetAwaiter().GetResult();
            return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<TraktDeviceCode>(raw);
        }

        public void ClearDeviceFlow()
        {
            _store.RemoveAsync(DeviceFlowKey).Wait();
            _store.RemoveAsync(PollIntervalKey).Wait();
        }
    }
}
