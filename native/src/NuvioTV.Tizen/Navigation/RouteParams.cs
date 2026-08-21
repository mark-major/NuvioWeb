using System;
using System.Collections.Generic;

namespace NuvioTV.Tizen.Navigation
{
    /// <summary>Typed bag of route parameters (the webapp passes plain objects).</summary>
    public sealed class RouteParams
    {
        private readonly Dictionary<string, object> _values =
            new Dictionary<string, object>(StringComparer.Ordinal);

        public RouteParams() { }

        public RouteParams(Dictionary<string, object> values)
        {
            if (values != null)
            {
                foreach (var kv in values)
                {
                    _values[kv.Key] = kv.Value;
                }
            }
        }

        public RouteParams Set(string key, object value)
        {
            _values[key] = value;
            return this;
        }

        public bool TryGet(string key, out object value) => _values.TryGetValue(key, out value);

        public string GetString(string key, string fallback = null) =>
            TryGet(key, out var v) && v != null ? v.ToString() : fallback;

        public int GetInt(string key, int fallback = 0) =>
            TryGet(key, out var v) && v != null && int.TryParse(v.ToString(), out var parsed) ? parsed : fallback;

        public bool GetBool(string key, bool fallback = false)
        {
            if (!TryGet(key, out var v) || v == null) return fallback;
            if (v is bool b) return b;
            return bool.TryParse(v.ToString(), out var parsed) ? parsed : fallback;
        }
    }
}
