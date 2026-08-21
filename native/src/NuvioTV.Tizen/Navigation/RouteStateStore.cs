using System;
using System.Collections.Generic;

namespace NuvioTV.Tizen.Navigation
{
    /// <summary>In-memory per-route state store (router.js routeState snapshots).</summary>
    public sealed class RouteStateStore
    {
        private readonly Dictionary<string, object> _state =
            new Dictionary<string, object>(StringComparer.Ordinal);

        public void Set(string key, object value) => _state[key] = value;

        public bool TryGet(string key, out object value) => _state.TryGetValue(key, out value);

        public object Get(string key) => _state.TryGetValue(key, out var v) ? v : null;

        public void Clear(string key) => _state.Remove(key);

        /// <summary>Removes every key starting with the prefix (route family resets).</summary>
        public void ClearByPrefix(string prefix)
        {
            var toRemove = new List<string>();
            foreach (var key in _state.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    toRemove.Add(key);
                }
            }
            foreach (var key in toRemove)
            {
                _state.Remove(key);
            }
        }
    }
}
