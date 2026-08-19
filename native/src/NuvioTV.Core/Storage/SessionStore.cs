using System;
using System.Threading.Tasks;

namespace NuvioTV.Core.Storage
{
    /// <summary>
    /// Session-specific token storage using IKeyValueStore.
    /// Handles access token, refresh token, and anonymous session flag.
    /// </summary>
    public static class SessionStore
    {
        private const string AccessTokenKey = "access_token";
        private const string RefreshTokenKey = "refresh_token";
        private const string IsAnonymousSessionKey = "is_anonymous_session";

        /// <summary>
        /// Normalizes token values by trimming whitespace and converting null/empty strings to null.
        /// </summary>
        private static string NormalizeToken(string value)
        {
            var text = (value ?? "").Trim();
            if (string.IsNullOrEmpty(text) || text == "null" || text == "undefined")
            {
                return null;
            }
            return text;
        }

        /// <summary>
        /// Gets the current access token from storage.
        /// </summary>
        public static async Task<string> GetAccessTokenAsync(IKeyValueStore store)
        {
            var value = await store.GetAsync(AccessTokenKey);
            return NormalizeToken(value);
        }

        /// <summary>
        /// Sets the access token in storage.
        /// </summary>
        public static Task SetAccessTokenAsync(IKeyValueStore store, string value)
        {
            var normalized = NormalizeToken(value);
            if (string.IsNullOrEmpty(normalized))
            {
                return store.RemoveAsync(AccessTokenKey);
            }
            return store.SetAsync(AccessTokenKey, normalized);
        }

        /// <summary>
        /// Gets the current refresh token from storage.
        /// </summary>
        public static async Task<string> GetRefreshTokenAsync(IKeyValueStore store)
        {
            var value = await store.GetAsync(RefreshTokenKey);
            return NormalizeToken(value);
        }

        /// <summary>
        /// Sets the refresh token in storage.
        /// </summary>
        public static Task SetRefreshTokenAsync(IKeyValueStore store, string value)
        {
            var normalized = NormalizeToken(value);
            if (string.IsNullOrEmpty(normalized))
            {
                return store.RemoveAsync(RefreshTokenKey);
            }
            return store.SetAsync(RefreshTokenKey, normalized);
        }

        /// <summary>
        /// Gets whether the current session is anonymous.
        /// </summary>
        public static async Task<bool> GetIsAnonymousSessionAsync(IKeyValueStore store)
        {
            var value = await store.GetAsync(IsAnonymousSessionKey);
            return value == "1";
        }

        /// <summary>
        /// Sets the anonymous session flag.
        /// </summary>
        public static Task SetIsAnonymousSessionAsync(IKeyValueStore store, bool value)
        {
            if (value)
            {
                return store.SetAsync(IsAnonymousSessionKey, "1");
            }
            else
            {
                return store.RemoveAsync(IsAnonymousSessionKey);
            }
        }

        /// <summary>
        /// Clears all session data from storage.
        /// </summary>
        public static async Task ClearAsync(IKeyValueStore store)
        {
            await store.RemoveAsync(AccessTokenKey);
            await store.RemoveAsync(RefreshTokenKey);
            await store.RemoveAsync(IsAnonymousSessionKey);
        }

        // Convenience sync methods for tests
        /// <summary>
        /// Synchronous version of GetAccessToken for testing convenience.
        /// </summary>
        public static string GetAccessToken(IKeyValueStore store)
        {
            return GetAccessTokenAsync(store).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous version of SetAccessToken for testing convenience.
        /// </summary>
        public static void SetAccessToken(IKeyValueStore store, string value)
        {
            SetAccessTokenAsync(store, value).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous version of GetIsAnonymousSession for testing convenience.
        /// </summary>
        public static bool GetIsAnonymousSession(IKeyValueStore store)
        {
            return GetIsAnonymousSessionAsync(store).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Synchronous version of SetIsAnonymousSession for testing convenience.
        /// </summary>
        public static void SetIsAnonymousSession(IKeyValueStore store, bool value)
        {
            SetIsAnonymousSessionAsync(store, value).GetAwaiter().GetResult();
        }
    }
}
