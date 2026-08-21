using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuvioTV.Tizen.Platform
{
    /// <summary>
    /// File-based IKeyValueStore implementation for Tizen platform.
    /// One file per key under <appdata>/store/, with atomic writes (tmp+rename),
    /// read-through memory cache, and debounced flush (250ms).
    /// </summary>
    public sealed class JsonFileStore : NuvioTV.Core.Storage.IKeyValueStore, IDisposable
    {
        private const string StoreDirectoryName = "store";
        private const int FlushDebounceMs = 250;
        
        private readonly string _storeDirectory;
        private readonly Dictionary<string, string> _memoryCache = new Dictionary<string, string>();
        private readonly Dictionary<string, Task> _pendingFlushTasks = new Dictionary<string, Task>();
        private readonly SemaphoreSlim _flushSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Creates a new JsonFileStore with the specified application data directory.
        /// </summary>
        /// <param name="appDataPath">The application data directory path.</param>
        public JsonFileStore(string appDataPath)
        {
            if (string.IsNullOrEmpty(appDataPath))
            {
                throw new ArgumentException("Application data path cannot be null or empty", nameof(appDataPath));
            }

            _storeDirectory = Path.Combine(appDataPath, StoreDirectoryName);
            
            // Ensure store directory exists
            if (!Directory.Exists(_storeDirectory))
            {
                Directory.CreateDirectory(_storeDirectory);
            }
        }

        /// <summary>
        /// Gets the file path for a given key.
        /// </summary>
        private string GetFilePath(string key)
        {
            // Sanitize key to be used as filename
            var safeKey = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storeDirectory, $"{safeKey}.json");
        }

        /// <summary>
        /// Gets the temporary file path for atomic write operations.
        /// </summary>
        private string GetTempFilePath(string key)
        {
            var safeKey = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storeDirectory, $"{safeKey}.tmp");
        }

        /// <summary>
        /// Reads a value from storage, checking memory cache first.
        /// </summary>
        public async Task<string> GetAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            // Check memory cache first (read-through)
            if (_memoryCache.TryGetValue(key, out var cachedValue))
            {
                return cachedValue;
            }

            // Read from file
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                var fileContent = await File.ReadAllTextAsync(filePath);
                
                // Update memory cache
                _memoryCache[key] = fileContent;
                
                return fileContent;
            }
            catch (IOException)
            {
                // On read error, return null
                return null;
            }
        }

        /// <summary>
        /// Sets a value in storage with debounced flush to disk.
        /// </summary>
        public async Task SetAsync(string key, string json)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            if (json == null)
            {
                await RemoveAsync(key);
                return;
            }

            // Update memory cache immediately
            _memoryCache[key] = json;

            // Debounce flush to disk
            await DebouncedFlushAsync(key);
        }

        /// <summary>
        /// Removes a value from storage.
        /// </summary>
        public async Task RemoveAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key cannot be null or empty", nameof(key));
            }

            // Remove from memory cache
            _memoryCache.Remove(key);

            // Cancel any pending flush for this key
            if (_pendingFlushTasks.TryGetValue(key, out var pendingTask))
            {
                _pendingFlushTasks.Remove(key);
                // We don't await the cancelled task - let it complete naturally
            }

            // Delete file if it exists
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException)
                {
                    // Silently fail on delete errors
                }
            }
        }

        /// <summary>
        /// Performs a debounced flush of a key's value to disk.
        /// </summary>
        private async Task DebouncedFlushAsync(string key)
        {
            await _flushSemaphore.WaitAsync();
            try
            {
                // Cancel any existing pending flush for this key
                if (_pendingFlushTasks.TryGetValue(key, out var existingTask))
                {
                    _pendingFlushTasks.Remove(key);
                }

                // Create new debounced flush task
                var flushTask = Task.Delay(FlushDebounceMs).ContinueWith(async _ =>
                {
                    await FlushToDiskAsync(key);
                }).Unwrap();

                _pendingFlushTasks[key] = flushTask;
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        /// <summary>
        /// Flushes a key's value to disk using atomic write (tmp + rename).
        /// </summary>
        private async Task FlushToDiskAsync(string key)
        {
            if (!_memoryCache.TryGetValue(key, out var value))
            {
                return;
            }

            var filePath = GetFilePath(key);
            var tempFilePath = GetTempFilePath(key);

            try
            {
                // Write to temporary file first
                await File.WriteAllTextAsync(tempFilePath, value);

                // Atomic rename (replace)
                if (File.Exists(filePath))
                {
                    File.Replace(tempFilePath, filePath, null);
                }
                else
                {
                    File.Move(tempFilePath, filePath);
                }
            }
            catch (IOException)
            {
                // Silently fail on write errors
                // Clean up temp file if it exists
                if (File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch (IOException)
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }

        /// <summary>
        /// Flushes all pending writes to disk immediately.
        /// </summary>
        public async Task FlushAllAsync()
        {
            var keys = _memoryCache.Keys.ToList();
            var tasks = new List<Task>();

            foreach (var key in keys)
            {
                if (_pendingFlushTasks.TryGetValue(key, out var pendingTask))
                {
                    tasks.Add(pendingTask);
                }
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Disposes resources used by the store.
        /// </summary>
        public void Dispose()
        {
            // Flush all pending writes before disposal
            try
            {
                FlushAllAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore flush errors during disposal
            }

            _flushSemaphore?.Dispose();
        }
    }
}
