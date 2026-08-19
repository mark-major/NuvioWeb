using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// High-level local storage with JSON serialization over IKeyValueStore.
    /// Provides typed Get/Set operations and raw string storage.
    /// </summary>
    public static class LocalStore
    {
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Gets a deserialized value from storage, or default if not found.
        /// </summary>
        public static async Task<T> GetAsync<T>(string key, IKeyValueStore store, T defaultValue = default)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            try
            {
                var json = await store.GetAsync(key);
                if (string.IsNullOrEmpty(json))
                {
                    return defaultValue!;
                }

                var result = JsonSerializer.Deserialize<T>(json, _jsonOptions);
                return result ?? defaultValue;
            }
            catch (JsonException)
            {
                return defaultValue!;
            }
        }

        /// <summary>
        /// Serializes and sets a value in storage.
        /// </summary>
        public static Task SetAsync<T>(string key, T value, IKeyValueStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            try
            {
                var json = JsonSerializer.Serialize(value, _jsonOptions);
                return store.SetAsync(key, json);
            }
            catch (JsonException)
            {
                // Silently fail on serialization errors
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Removes a value from storage.
        /// </summary>
        public static Task RemoveAsync(string key, IKeyValueStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            return store.RemoveAsync(key);
        }
    }
}