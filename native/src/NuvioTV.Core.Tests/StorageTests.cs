using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Storage;
using Xunit;

namespace NuvioTV.Core.Tests
{
    public class StorageTests
    {
        // Helper: In-memory store for tests
        private class TestKeyValueStore : IKeyValueStore
        {
            private readonly System.Collections.Generic.Dictionary<string, string> _data = 
                new System.Collections.Generic.Dictionary<string, string>();

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

        [Fact]
        public async Task ProfileScopedStore_EmptyEnvelope_WrapsAndUnwraps()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key",
                store,
                item => item ?? new TestItem(),
                (current, partial) => { 
                    current = current ?? new TestItem(); 
                    partial = partial ?? new TestItem(); 
                    return new TestItem { Value = current.Value + partial.Value }; 
                }
            );

            // Act
            var result = await profileStore.GetAsync("profile1");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(0, result.Value);
        }

        [Fact]
        public async Task ProfileScopedStore_LegacyValue_MigratesToAllProfiles()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var legacyValue = new TestItem { Value = 42 };
            
            // First, write a legacy (non-envelope) value directly to the store
            var legacyJson = System.Text.Json.JsonSerializer.Serialize(legacyValue);
            await store.SetAsync("test-key-legacy", legacyJson);

            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key-legacy",
                store,
                item => item ?? new TestItem(),
                (current, partial) => { 
                    current = current ?? new TestItem(); 
                    partial = partial ?? new TestItem(); 
                    return new TestItem { Value = current.Value + partial.Value }; 
                }
            );

            // Act
            var profile1Value = await profileStore.GetAsync("1");
            var profile2Value = await profileStore.GetAsync("2");

            // Assert
            Assert.NotNull(profile1Value);
            Assert.NotNull(profile2Value);
            Assert.Equal(42, profile1Value.Value);
            Assert.Equal(42, profile2Value.Value);
        }

        [Fact]
        public async Task ProfileScopedStore_PerProfileIsolation()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key-isolation",
                store,
                item => item ?? new TestItem(),
                (current, partial) => { 
                    current = current ?? new TestItem(); 
                    partial = partial ?? new TestItem(); 
                    return new TestItem { Value = current.Value + partial.Value }; 
                }
            );

            // Act
            await profileStore.ReplaceAsync("profile1", new TestItem { Value = 10 });
            await profileStore.ReplaceAsync("profile2", new TestItem { Value = 20 });

            var profile1Value = await profileStore.GetAsync("profile1");
            var profile2Value = await profileStore.GetAsync("profile2");

            // Assert
            Assert.Equal(10, profile1Value.Value);
            Assert.Equal(20, profile2Value.Value);
        }

        [Fact]
        public void StorageCaps_Enforce_KeepsNewest()
        {
            // Arrange
            var items = new[]
            {
                new TestItem { UpdatedAt = new DateTime(2024, 1, 1), Value = 1 },
                new TestItem { UpdatedAt = new DateTime(2024, 1, 2), Value = 2 },
                new TestItem { UpdatedAt = new DateTime(2024, 1, 3), Value = 3 },
                new TestItem { UpdatedAt = new DateTime(2024, 1, 4), Value = 4 },
                new TestItem { UpdatedAt = new DateTime(2024, 1, 5), Value = 5 }
            };

            // Act - keep only 3 newest
            var result = StorageCaps.Enforce(items, 3, item => item.UpdatedAt).ToArray();

            // Assert
            Assert.Equal(3, result.Length);
            Assert.Equal(5, result[0].Value); // Newest
            Assert.Equal(4, result[1].Value);
            Assert.Equal(3, result[2].Value);
        }

        [Fact]
        public void StorageCaps_HomeImageCache_Enforces30DayRule()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var items = new[]
            {
                new TestItem { UpdatedAt = now.AddDays(-35), Value = 1 },  // Too old
                new TestItem { UpdatedAt = now.AddDays(-25), Value = 2 },  // Within 30 days
                new TestItem { UpdatedAt = now.AddDays(-15), Value = 3 },  // Within 30 days
                new TestItem { UpdatedAt = now.AddDays(-5), Value = 4 }   // Recent
            };

            // Act - enforce 500 items cap and 30-day age limit
            var result = StorageCaps.EnforceHomeImageCache(items).ToArray();

            // Assert - oldest items (35 days) excluded, newest items kept in descending order
            Assert.Equal(3, result.Length);
            Assert.Equal(4, result[0].Value); // Most recent
            Assert.Equal(3, result[1].Value); // Middle
            Assert.Equal(2, result[2].Value); // Oldest within 30 days
        }

        [Fact]
        public async Task LocalStore_SerializeDeserialize()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var testItem = new TestItem { Value = 123 };

            // Act
            await LocalStore.SetAsync("serialize-test", testItem, store);
            var deserialized = await LocalStore.GetAsync<TestItem>("serialize-test", store);

            // Assert
            Assert.NotNull(deserialized);
            Assert.Equal(123, deserialized.Value);
        }

        [Fact]
        public async Task LocalStore_Remove()
        {
            // Arrange
            var store = new TestKeyValueStore();
            await LocalStore.SetAsync("remove-test", new TestItem { Value = 456 }, store);

            // Act
            await LocalStore.RemoveAsync("remove-test", store);
            var result = await LocalStore.GetAsync<TestItem>("remove-test", store);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task SessionStore_TokenOperations()
        {
            // Arrange
            var store = new TestKeyValueStore();

            // Act & Assert
            await SessionStore.SetAccessTokenAsync(store, "test-token");
            Assert.Equal("test-token", await SessionStore.GetAccessTokenAsync(store));

            await SessionStore.SetAccessTokenAsync(store, null);
            Assert.Null(await SessionStore.GetAccessTokenAsync(store));
        }

        [Fact]
        public async Task SessionStore_AnonymousSession()
        {
            // Arrange
            var store = new TestKeyValueStore();

            // Act & Assert
            await SessionStore.SetIsAnonymousSessionAsync(store, true);
            Assert.True(await SessionStore.GetIsAnonymousSessionAsync(store));

            await SessionStore.SetIsAnonymousSessionAsync(store, false);
            Assert.False(await SessionStore.GetIsAnonymousSessionAsync(store));
        }

        [Fact]
        public async Task ProfileScopedStore_SetMergesValues()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key-merge",
                store,
                item => item ?? new TestItem(),
                (current, partial) => { 
                    current = current ?? new TestItem(); 
                    partial = partial ?? new TestItem(); 
                    return new TestItem { Value = current.Value + partial.Value }; 
                }
            );

            // Act
            await profileStore.SetAsync("profile1", new TestItem { Value = 10 });
            await profileStore.SetAsync("profile1", new TestItem { Value = 5 });

            var result = await profileStore.GetAsync("profile1");

            // Assert - Should be 10 + 5 = 15 based on our merge function
            Assert.Equal(15, result.Value);
        }

        [Fact]
        public async Task ProfileScopedStore_ClearProfile()
        {
            // Arrange
            var store = new TestKeyValueStore();
            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key-clear",
                store,
                item => item ?? new TestItem(),
                (current, partial) => { 
                    current = current ?? new TestItem(); 
                    partial = partial ?? new TestItem(); 
                    return new TestItem { Value = current.Value + partial.Value }; 
                }
            );

            // Act
            await profileStore.SetAsync("profile1", new TestItem { Value = 100 });
            await profileStore.ClearProfileAsync("profile1");
            
            var result = await profileStore.GetAsync("profile1");

            // Assert - Should have a new default value (0) after clearing
            Assert.NotNull(result);
            Assert.Equal(0, result.Value);
        }
        [Fact]
        public async Task ProfileScopedStore_LegacyCamelCase_MigratesCorrectly()
        {
            // Arrange - Create a legacy unscoped value using camelCase serialization (as webapp would)
            var store = new InMemoryKeyValueStore();
            var legacyItem = new TestItem { Value = 42, UpdatedAt = DateTime.UtcNow };
            
            // Serialize using camelCase options to mimic webapp legacy format
            var legacyCamelCaseJson = JsonSerializer.Serialize(legacyItem, NuvioTV.Core.Storage.LocalStore.JsonOptions);
            await store.SetAsync("test-key", legacyCamelCaseJson);

            // Act - Read with ProfileScopedStore, triggering legacy migration
            var profileStore = ProfileScopedStore<TestItem>.CreateWithStore(
                "test-key",
                store,
                item => item ?? new TestItem(),
                (current, partial) => {
                    current = current ?? new TestItem();
                    partial = partial ?? new TestItem();
                    return new TestItem { Value = current.Value + partial.Value };
                }
            );

            var result = await profileStore.GetAsync("1"); // Default profile

            // Assert - The value should survive migration (not be lost to casing defaults)
            Assert.NotNull(result);
            Assert.Equal(42, result.Value); // Original value preserved
        }


        // Test helper class
        public class TestItem : IHasUpdatedAt
        {
            // camelCase names: storage layer persists webapp-shaped (camelCase) JSON.
            [System.Text.Json.Serialization.JsonPropertyName("value")]
            public int Value { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        }
    }
}
