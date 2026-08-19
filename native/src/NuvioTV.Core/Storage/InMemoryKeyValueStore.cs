using System.Collections.Generic;
using System.Threading.Tasks;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// In-memory key-value store for testing and development.
    /// Not thread-safe; use single-threaded test scenarios only.
    /// </summary>
    public sealed class InMemoryKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _data = new Dictionary<string, string>();

        public Task<string> GetAsync(string key)
        {
            return Task.FromResult(_data.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, string json)
        {
            _data[key] = json;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            _data.Remove(key);
            return Task.CompletedTask;
        }
    }
}
