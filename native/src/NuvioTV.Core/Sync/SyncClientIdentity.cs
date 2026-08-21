using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// Stable per-installation client identity used to attribute sync writes.
    ///
    /// Behavioral spec (do not diverge): js/core/sync/syncClientIdentity.js.
    /// The id is cached process-wide; a valid persisted value wins over a fresh
    /// generation, and storage failures degrade to a volatile identity so the
    /// RPC can still carry one.
    /// </summary>
    public static class SyncClientIdentity
    {
        private const string ClientIdKey = "nuvio_web_installation_id";
        private const string ClientIdPrefix = "nuvio-web-";
        private const int ClientIdLength = 32;
        private const string ClientIdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

        private static readonly object Gate = new object();
        private static string _volatileClientId;

        /// <summary>Test hook: clears the process-local identity.</summary>

        /// <summary>Test/integration hook: clears the process-local identity.</summary>
        public static void ResetForTests()
        {
            lock (Gate)
            {
                _volatileClientId = null;
            }
        }

        // JS parity: getSyncClientId (syncClientIdentity.js:30-52).
        public static async Task<string> GetSyncClientIdAsync(IKeyValueStore storage)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            string existing;
            lock (Gate)
            {
                existing = _volatileClientId;
            }

            if (existing != null)
            {
                return existing;
            }

            string stored = null;
            try
            {
                stored = await storage.GetAsync(ClientIdKey).ConfigureAwait(false);
                if (IsValidClientId(stored))
                {
                    var trimmed = stored.Trim();
                    lock (Gate)
                    {
                        _volatileClientId = trimmed;
                    }

                    return trimmed;
                }
            }
            catch
            {
                // Keep a process-local identity when persistent storage is unavailable.
            }

            var generated = GenerateClientId();
            try
            {
                await storage.SetAsync(ClientIdKey, generated).ConfigureAwait(false);
            }
            catch
            {
                // The RPC can still use the process-local identity.
            }

            lock (Gate)
            {
                _volatileClientId = generated;
            }

            return generated;
        }

        // JS parity: isValidClientId (syncClientIdentity.js:8-11).
        public static bool IsValidClientId(string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.Length < 16 || normalized.Length > 96)
            {
                return false;
            }

            foreach (var ch in normalized)
            {
                var valid =
                    (ch >= 'A' && ch <= 'Z') ||
                    (ch >= 'a' && ch <= 'z') ||
                    (ch >= '0' && ch <= '9') ||
                    ch == '_' || ch == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        // JS parity: generateClientId (syncClientIdentity.js:13-28).
        public static string GenerateClientId()
        {
            var bytes = new byte[ClientIdLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var suffix = new StringBuilder(ClientIdLength);
            foreach (var b in bytes)
            {
                suffix.Append(ClientIdAlphabet[b % ClientIdAlphabet.Length]);
            }

            return ClientIdPrefix + suffix;
        }
    }
}
