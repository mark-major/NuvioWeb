using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Sync
{
    /// <summary>
    /// One locally stored saved-library item (JS savedLibraryStore.js item shape).
    /// </summary>
    public sealed class SavedLibraryItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("profileId")]
        public string ProfileId { get; set; } = "1";

        [System.Text.Json.Serialization.JsonPropertyName("contentId")]
        public string ContentId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("contentType")]
        public string ContentType { get; set; } = "movie";

        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("poster")]
        public string Poster { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("posterShape")]
        public string PosterShape { get; set; } = "POSTER";

        [System.Text.Json.Serialization.JsonPropertyName("background")]
        public string Background { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("releaseInfo")]
        public string ReleaseInfo { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("imdbRating")]
        public double? ImdbRating { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("genres")]
        public List<string> Genres { get; set; } = new List<string>();

        [System.Text.Json.Serialization.JsonPropertyName("addonBaseUrl")]
        public string AddonBaseUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
        public long UpdatedAt { get; set; }
    }

    /// <summary>
    /// Remote sync of the saved library.
    ///
    /// Behavioral spec (do not diverge): js/core/profile/savedLibrarySyncService.js.
    /// Pull pages through sync_pull_library at 500 rows per offset page and REPLACES
    /// the local list for the profile (remote is authoritative); push skips when the
    /// local list is empty. The 500ms debounced cloud-push scheduling of
    /// js/data/repository/savedLibraryRepository.js (queueSavedLibraryCloudSync) is
    /// exposed as <see cref="SchedulePushAsync"/>.
    /// </summary>
    public sealed class SavedLibrarySyncService : IDisposable
    {
        private const string PullRpc = "sync_pull_library";
        private const string PushRpc = "sync_push_library";
        public const int PullPageSize = 500;
        public const int PushDebounceMs = 500;
        private const string StoreKey = "savedLibraryItems";
        private static readonly HashSet<string> ValidPosterShapes = new HashSet<string>
        {
            "POSTER", "LANDSCAPE", "SQUARE"
        };

        private readonly SupabaseClient _supabase;
        private readonly AuthManager _auth;
        private readonly ProfileManager _profiles;
        private readonly IKeyValueStore _store;

        private readonly object _debounceGate = new object();
        private Timer _debounceTimer;
        private TaskCompletionSource<bool> _pendingPush;
        private bool _disposed;

        public SavedLibrarySyncService(
            SupabaseClient supabase,
            AuthManager auth,
            ProfileManager profiles,
            IKeyValueStore store)
        {
            _supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // -------------------------------------------------------------------
        // Mapping (savedLibrarySyncService.js:11-68)
        // -------------------------------------------------------------------

        // JS parity: normalizePosterShape — POSTER/LANDSCAPE/SQUARE, else POSTER.
        public static string NormalizePosterShape(string value)
        {
            var shape = (value ?? "").Trim().ToUpperInvariant();
            return ValidPosterShapes.Contains(shape) ? shape : "POSTER";
        }

        // JS parity: mapRemoteItem.
        public static SavedLibraryItem MapRemoteItem(JsonElement row)
        {
            var updatedAtRaw = FirstPresent(
                SyncJson.GetProperty(row, "added_at"),
                SyncJson.GetProperty(row, "addedAt"),
                SyncJson.GetProperty(row, "updated_at"),
                SyncJson.GetProperty(row, "updatedAt"),
                SyncJson.GetProperty(row, "created_at"),
                SyncJson.GetProperty(row, "createdAt"));

            var updatedAtNumber = SyncJson.CoerceNumber(updatedAtRaw);

            var genres = new List<string>();
            JsonElement genresElement;
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("genres", out genresElement) &&
                genresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var genre in genresElement.EnumerateArray())
                {
                    if (genre.ValueKind == JsonValueKind.String)
                    {
                        genres.Add(genre.GetString());
                    }
                    else if (genre.ValueKind == JsonValueKind.Number)
                    {
                        genres.Add(genre.GetRawText());
                    }
                }
            }

            double? imdbRating = null;
            JsonElement ratingElement;
            if (row.ValueKind == JsonValueKind.Object &&
                (row.TryGetProperty("imdb_rating", out ratingElement) ||
                 row.TryGetProperty("imdbRating", out ratingElement)) &&
                ratingElement.ValueKind == JsonValueKind.Number)
            {
                imdbRating = ratingElement.GetDouble();
            }

            long updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (updatedAtNumber.HasValue)
            {
                updatedAt = (long)Math.Truncate(updatedAtNumber.Value);
            }

            return new SavedLibraryItem
            {
                ContentId = FirstNonEmpty(
                    FirstNonEmpty(SyncJson.GetStringOrNull(row, "content_id", "contentId"),
                        SyncJson.GetStringOrNull(row, "id")), ""),
                ContentType = SyncJson.GetStringOrNull(row, "content_type", "contentType") ?? "movie",
                Title = FirstNonEmpty(SyncJson.GetStringOrNull(row, "name", "title"), "Untitled"),
                Poster = SyncJson.GetStringOrNull(row, "poster"),
                PosterShape = NormalizePosterShape(
                    FirstNonEmpty(SyncJson.GetStringOrNull(row, "poster_shape", "posterShape"), null)),
                Background = SyncJson.GetStringOrNull(row, "background"),
                Description = SyncJson.GetStringOrNull(row, "description") ?? "",
                ReleaseInfo = FirstNonEmpty(SyncJson.GetStringOrNull(row, "release_info", "releaseInfo"), ""),
                ImdbRating = imdbRating,
                Genres = genres,
                AddonBaseUrl = FirstNonEmpty(SyncJson.GetStringOrNull(row, "addon_base_url", "addonBaseUrl"), null),
                UpdatedAt = updatedAt
            };
        }

        // JS parity: toRemoteItem — snake_case wire shape with added_at carrying
        // the item's last update time.
        public static Dictionary<string, object> ToRemoteItem(SavedLibraryItem item)
        {
            return new Dictionary<string, object>
            {
                ["content_id"] = item.ContentId,
                ["content_type"] = string.IsNullOrEmpty(item.ContentType) ? "movie" : item.ContentType,
                ["name"] = FirstNonEmpty(item.Title, "Untitled"),
                ["poster_shape"] = NormalizePosterShape(item.PosterShape),
                ["background"] = string.IsNullOrEmpty(item.Background) ? null : item.Background,
                ["description"] = item.Description ?? "",
                ["release_info"] = item.ReleaseInfo ?? "",
                ["imdb_rating"] = item.ImdbRating.HasValue ? (object)item.ImdbRating.Value : null,
                ["genres"] = (item.Genres ?? new List<string>()).ToArray(),
                ["addon_base_url"] = string.IsNullOrEmpty(item.AddonBaseUrl) ? null : item.AddonBaseUrl,
                ["added_at"] = (double)(item.UpdatedAt > 0
                    ? item.UpdatedAt
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            };
        }

        // -------------------------------------------------------------------
        // Local persistence (savedLibraryStore.js semantics)
        // -------------------------------------------------------------------

        private async Task<List<SavedLibraryItem>> ReadAllStoredAsync()
        {
            try
            {
                var json = await _store.GetAsync(StoreKey).ConfigureAwait(false);
                if (string.IsNullOrEmpty(json))
                {
                    return new List<SavedLibraryItem>();
                }

                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return new List<SavedLibraryItem>();
                    }

                    var items = new List<SavedLibraryItem>();
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        try
                        {
                            var item = JsonSerializer.Deserialize<SavedLibraryItem>(
                                element.GetRawText(), LocalStore.JsonOptions);
                            if (item != null)
                            {
                                items.Add(item);
                            }
                        }
                        catch (JsonException)
                        {
                            // Skip malformed entries.
                        }
                    }

                    items.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
                    return items;
                }
            }
            catch (JsonException)
            {
                return new List<SavedLibraryItem>();
            }
        }

        public async Task<IReadOnlyList<SavedLibraryItem>> ListForProfileAsync(string profileId, int limit)
        {
            var pid = NormalizeProfileId(profileId);
            var all = await ReadAllStoredAsync().ConfigureAwait(false);
            var filtered = all.Where(item => (item.ProfileId ?? "1") == pid).ToList();
            return limit > 0 && filtered.Count > limit ? filtered.GetRange(0, limit) : filtered;
        }

        // JS parity: replaceForProfile — other profiles' entries are untouched.
        public async Task ReplaceForProfileAsync(string profileId, IEnumerable<SavedLibraryItem> items)
        {
            var pid = NormalizeProfileId(profileId);
            var normalized = (items ?? Enumerable.Empty<SavedLibraryItem>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.ContentId))
                .Select(item => new SavedLibraryItem
                {
                    ProfileId = pid,
                    ContentId = item.ContentId ?? "",
                    ContentType = item.ContentType ?? "movie",
                    Title = item.Title ?? "",
                    Poster = item.Poster,
                    PosterShape = NormalizePosterShape(item.PosterShape),
                    Background = item.Background,
                    Description = item.Description ?? "",
                    ReleaseInfo = item.ReleaseInfo ?? "",
                    ImdbRating = item.ImdbRating,
                    Genres = item.Genres ?? new List<string>(),
                    AddonBaseUrl = item.AddonBaseUrl,
                    UpdatedAt = item.UpdatedAt > 0
                        ? item.UpdatedAt
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                })
                .ToList();

            var all = await ReadAllStoredAsync().ConfigureAwait(false);
            var next = normalized.Concat(all.Where(item => (item.ProfileId ?? "1") != pid)).ToList();
            next.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
            await LocalStore.SetAsync(StoreKey, next.Take(5000).ToList(), _store).ConfigureAwait(false);
        }

        // -------------------------------------------------------------------
        // Pull / push (savedLibrarySyncService.js:70-133)
        // -------------------------------------------------------------------

        public async Task<IReadOnlyList<SavedLibraryItem>> PullAsync(string profileId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return new List<SavedLibraryItem>();
                }

                var resolvedProfileId = NormalizeProfileId(profileId ?? await _profiles.GetActiveProfileId().ConfigureAwait(false));
                var localItems = await ListForProfileAsync(resolvedProfileId, 1000).ConfigureAwait(false);

                var rows = new List<JsonElement>();
                for (int offset = 0; ; offset += PullPageSize)
                {
                    var page = await _supabase.RpcAsync(PullRpc, new Dictionary<string, object>
                    {
                        ["p_profile_id"] = ParseProfileNumber(resolvedProfileId),
                        ["p_limit"] = PullPageSize,
                        ["p_offset"] = offset
                    }, true, ct).ConfigureAwait(false);

                    var pageRows = page.ValueKind == JsonValueKind.Array
                        ? page.EnumerateArray().ToList()
                        : new List<JsonElement>();
                    rows.AddRange(pageRows);
                    if (pageRows.Count < PullPageSize)
                    {
                        break;
                    }
                }

                var remoteItems = rows
                    .Select(MapRemoteItem)
                    .Where(item => !string.IsNullOrEmpty(item.ContentId))
                    .ToList();

                if (remoteItems.Count == 0 && localItems.Count > 0)
                {
                    return localItems;
                }

                await ReplaceForProfileAsync(resolvedProfileId, remoteItems).ConfigureAwait(false);
                return remoteItems;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return new List<SavedLibraryItem>();
            }
        }

        public async Task<bool> PushAsync(string profileId = null, CancellationToken ct = default)
        {
            try
            {
                if (!_auth.IsAuthenticated)
                {
                    return false;
                }

                var resolvedProfileId = NormalizeProfileId(profileId ?? await _profiles.GetActiveProfileId().ConfigureAwait(false));
                var items = await ListForProfileAsync(resolvedProfileId, 1000).ConfigureAwait(false);
                if (items.Count == 0)
                {
                    return true;
                }

                await _supabase.RpcAsync(PushRpc, new Dictionary<string, object>
                {
                    ["p_profile_id"] = ParseProfileNumber(resolvedProfileId),
                    ["p_items"] = items.Select(ToRemoteItem).ToArray()
                }, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                return false;
            }
        }

        // -------------------------------------------------------------------
        // Debounced push (savedLibraryRepository.js queueSavedLibraryCloudSync)
        // -------------------------------------------------------------------

        /// <summary>
        /// Schedules a debounced push for the profile: repeated calls within the
        /// window replace the pending timer, so only one push runs after quiet.
        /// </summary>
        public Task<bool> SchedulePushAsync(string profileId = null, int? debounceMs = null)
        {
            var resolved = NormalizeProfileId(profileId ?? _profiles.GetActiveProfileId().GetAwaiter().GetResult());
            var delay = Math.Max(0, debounceMs.GetValueOrDefault(PushDebounceMs));
            lock (_debounceGate)
            {
                if (_disposed)
                {
                    return Task.FromResult(false);
                }

                if (_debounceTimer != null)
                {
                    _debounceTimer.Dispose();
                }

                if (_pendingPush == null)
                {
                    _pendingPush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                var completion = _pendingPush;
                _debounceTimer = new Timer(_ =>
                {
                    bool result;
                    lock (_debounceGate)
                    {
                        _debounceTimer = null;
                        _pendingPush = null;
                    }

                    try
                    {
                        result = PushAsync(resolved).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        result = false;
                    }

                    completion.TrySetResult(result);
                }, null, delay, Timeout.Infinite);
                return completion.Task;
            }
        }

        public void Dispose()
        {
            lock (_debounceGate)
            {
                _disposed = true;
                if (_debounceTimer != null)
                {
                    _debounceTimer.Dispose();
                    _debounceTimer = null;
                }
            }
        }

        // -------------------------------------------------------------------

        private static string NormalizeProfileId(string profileId)
        {
            var trimmed = (profileId ?? "").Trim();
            return trimmed.Length == 0 ? "1" : trimmed;
        }

        // JS Number coercion for RPC profile ids; unparsable → 1.
        private static long ParseProfileNumber(string profileId)
        {
            long parsed;
            return long.TryParse((profileId ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0
                ? parsed
                : 1;
        }

        private static JsonElement FirstPresent(params JsonElement[] values)
        {
            foreach (var value in values)
            {
                if (value.ValueKind != JsonValueKind.Undefined && value.ValueKind != JsonValueKind.Null)
                {
                    return value;
                }
            }

            return default;
        }

        private static string FirstNonEmpty(string left, string fallback)
        {
            return !string.IsNullOrEmpty(left) ? left : fallback;
        }
    }
}
