using System.Threading.Tasks;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// Low-level key-value store interface for string storage.
    /// Implementations handle persistence (in-memory, file system, etc.).
    /// </summary>
    public interface IKeyValueStore
    {
        /// <summary>
        /// Gets the raw string value for a given key.
        /// </summary>
        /// <param name="key">The storage key.</param>
        /// <returns>The stored string value, or null if not found.</returns>
        Task<string> GetAsync(string key);

        /// <summary>
        /// Sets the raw string value for a given key.
        /// </summary>
        /// <param name="key">The storage key.</param>
        /// <param name="json">The string value to store.</param>
        Task SetAsync(string key, string json);

        /// <summary>
        /// Removes the value for a given key.
        /// </summary>
        /// <param name="key">The storage key.</param>
        Task RemoveAsync(string key);
    }
}
