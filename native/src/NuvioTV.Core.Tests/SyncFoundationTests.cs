using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Storage;
using NuvioTV.Core.Sync;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for the sync foundation: WatchProgressSyncService (3-way
    /// merge, row mapping, sync shaping), SavedLibrarySyncService mapping and
    /// paged pull, StartupSyncService orchestration gating.
    /// JS sources: watchProgressSyncService.js / savedLibrarySyncService.js /
    /// startupSyncService.js.
    /// </summary>
    [Collection("StaticConfig")]
    public class SyncFoundationTests : IDisposable
    {
        private readonly MemoryKeyValueStore _store = new MemoryKeyValueStore();
        private RecordingHandler _handler;
        private HttpClient _httpClient;

        public SyncFoundationTests()
        {
            AppConfig.ResetForTests();
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["supabaseUrl"] = "https://primary.supabase.co",
                ["supabaseAnonKey"] = "anon-test-key"
            });
            AppConfig.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            AppConfig.ResetForTests();
        }

        private SupabaseClient CreateClient()
        {
            _handler = new RecordingHandler();
            _httpClient = new HttpClient(_handler);
            return new SupabaseClient(
                _httpClient, new StubSessionTokenProvider("session-token", "refresh-token"));
        }

        private async Task<AuthManager> CreateAuthenticatedAuthAsync()
        {
            await SessionStore.SetAccessTokenAsync(
                _store, JwtGenerator.GenerateToken(expiresInSeconds: 3600));
            await SessionStore.SetRefreshTokenAsync(_store, "rt");
            var auth = new AuthManager(_httpClient, _store);
            await auth.InitializeAsync();
            Assert.Equal(AuthState.Authenticated, auth.State);
            return auth;
        }

        private static HttpResponseMessage JsonArrayResponse(params object[] rows)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json")
            };
        }

        // -----------------------------------------------------------------
        // WatchProgressSyncService — pure helpers
        // ------------------------------------------------------------------

        private static ProgressSyncItem Item(
            string contentId,
            long? season = null,
            long? episode = null,
            string videoId = null,
            long positionMs = 100,
            long updatedAt = 100)
        {
            return new ProgressSyncItem
            {
                ContentId = contentId,
                ContentType = season.HasValue ? "series" : "movie",
                VideoId = videoId,
                Season = season,
                Episode = episode,
                PositionMs = positionMs,
                DurationMs = 200,
                UpdatedAt = updatedAt
            };
        }

        [Fact]
        public void ProgressSync_ThreeWayMerge_LocalChangedWins()
        {
            var baseline = Item("m1", positionMs: 50, updatedAt: 100);
            var local = Item("m1", positionMs: 80, updatedAt: 200);

            var merged = WatchProgressSyncService.MergeProgressItems(
                new[] { local }, Array.Empty<ProgressSyncItem>(), new[] { baseline });

            var item = Assert.Single(merged);
            Assert.Equal(80, item.PositionMs);
        }

        [Fact]
        public void ProgressSync_ThreeWayMerge_RemoteChangeKeepsLocalMetadata()
        {
            var baseline = Item("m1", positionMs: 50, updatedAt: 100);
            var local = Item("m1", positionMs: 50, updatedAt: 100);
            local.Title = "Local Title";
            var remote = Item("m1", positionMs: 90, updatedAt: 300);

            var merged = WatchProgressSyncService.MergeProgressItems(
                new[] { local }, new[] { remote }, new[] { baseline });

            var item = Assert.Single(merged);
            Assert.Equal(90, item.PositionMs);       // portable fields from remote
            Assert.Equal("Local Title", item.Title); // display metadata preserved
        }

        [Fact]
        public void ProgressSync_ThreeWayMerge_BothChangedNewerWins()
        {
            var baseline = Item("m1", positionMs: 10, updatedAt: 100);
            var localOlder = Item("m1", positionMs: 20, updatedAt: 150);
            var remoteNewer = Item("m1", positionMs: 30, updatedAt: 500);

            var merged = WatchProgressSyncService.MergeProgressItems(
                new[] { localOlder }, new[] { remoteNewer }, new[] { baseline });

            var item = Assert.Single(merged);
            Assert.Equal(30, item.PositionMs);
            Assert.Equal(500, item.UpdatedAt);
        }

        [Fact]
        public void ProgressSync_ThreeWayMerge_BaselinePrunesUnchangedRemoteOnly()
        {
            var baseline = Item("m1", positionMs: 50, updatedAt: 100);

            var merged = WatchProgressSyncService.MergeProgressItems(
                Array.Empty<ProgressSyncItem>(),
                new[] { Item("m1", positionMs: 50, updatedAt: 100) },
                new[] { baseline });

            Assert.Empty(merged); // unchanged since baseline → not reapplied locally
        }

        [Fact]
        public void ProgressSync_MapRow_SnakeCaseAndTraktCompletion()
        {
            var row = JsonDocument.Parse(@"{
                ""content_id"": ""tt123"",
                ""content_type"": ""series"",
                ""video_id"": ""tt123"",
                ""season"": 2,
                ""episode"": 7,
                ""position_ms"": 60000,
                ""duration_ms"": 120000,
                ""progress_percent"": 42.5,
                ""source"": ""trakt_history"",
                ""updated_at"": 1700000000000
            }").RootElement;

            var item = WatchProgressSyncService.MapProgressRow(row);

            Assert.Equal("tt123", item.ContentId);
            Assert.Null(item.VideoId);               // == contentId → nulled
            Assert.Equal(2, item.Season);
            Assert.Equal(7, item.Episode);
            Assert.Equal(60000, item.PositionMs);
            Assert.Equal(120000, item.DurationMs);
            Assert.Equal(100, item.ProgressPercent); // trakt_history <100 → 100
        }

        [Fact]
        public void ProgressSync_MapRow_AmbiguousSecondsInflatedDurationDivides()
        {
            var row = JsonDocument.Parse(@"{
                ""content_id"": ""m"",
                ""position"": 600,
                ""duration"": 5400
            }").RootElement;

            var item = WatchProgressSyncService.MapProgressRow(row);

            // No explicit *_ms fields; values ≤ 8h are treated as seconds.
            Assert.Equal(600000, item.PositionMs);
            Assert.Equal(5400000, item.DurationMs);
        }

        [Fact]
        public void ProgressSync_NormalizeInflatedTimes_DividesOnlyWhenPlausible()
        {
            var inflated = WatchProgressSyncService.NormalizeInflatedProgressTimes(
                600000, 86400000 * 2); // 48h duration → divide by 1000
            Assert.Equal((600, 172800), (inflated.Item1, inflated.Item2));

            var sane = WatchProgressSyncService.NormalizeInflatedProgressTimes(60, 120);
            Assert.Equal((60, 120), (sane.Item1, sane.Item2));
        }

        [Fact]
        public void ProgressSync_RemoteVideoIdAndKeys()
        {
            Assert.Equal("vid", WatchProgressSyncService.ToRemoteVideoId(
                new ProgressSyncItem { ContentId = "c", VideoId = "vid" }));
            Assert.Equal("__nuvio_episode__:2:3", WatchProgressSyncService.ToRemoteVideoId(
                new ProgressSyncItem { ContentId = "c", Season = 2, Episode = 3 }));
            // js: a bare contentId is returned as the remote video id ("main" only
            // when there is no contentId at all).
            Assert.Equal("c", WatchProgressSyncService.ToRemoteVideoId(
                new ProgressSyncItem { ContentId = "c" }));

            Assert.Equal("c_s2e3", WatchProgressSyncService.ToProgressKey(
                new ProgressSyncItem { ContentId = "c", Season = 2, Episode = 3 }));
            Assert.Equal("c", WatchProgressSyncService.ToProgressKey(
                new ProgressSyncItem { ContentId = "c" }));

            Assert.Equal("c:episode:2:3", WatchProgressSyncService.SyncIdentityKey(
                new ProgressSyncItem { ContentId = "c", Season = 2, Episode = 3 }));
        }

        [Fact]
        public void ProgressSync_CoalesceDedupesEpisodeIdentityKeepingNewest()
        {
            var items = new[]
            {
                Item("c", season: 1, episode: 1, videoId: "v-a", updatedAt: 100),
                Item("c", season: 1, episode: 1, videoId: "v-b", updatedAt: 200),
                Item("c", videoId: "main", updatedAt: 50),
            };

            var coalesced = WatchProgressSyncService.CoalesceSyncItems(items).ToList();

            Assert.Equal(2, coalesced.Count); // episode identity collapses to newest
            var episodeRow = Assert.Single(coalesced, item => item.Season.HasValue);
            Assert.Equal("v-b", episodeRow.VideoId);
            Assert.Equal(200, episodeRow.UpdatedAt);
        }

        [Fact]
        public void ProgressSync_MinDurationGate()
        {
            Assert.False(WatchProgressSyncService.IsSyncableProgressItem(
                new ProgressSyncItem { DurationMs = 59999 }));
            Assert.True(WatchProgressSyncService.IsSyncableProgressItem(
                new ProgressSyncItem { DurationMs = 60000 }));
            Assert.True(WatchProgressSyncService.IsSyncableProgressItem(
                new ProgressSyncItem { DurationMs = 0 })); // unknown duration still syncs
        }

        [Fact]
        public void ProgressSync_BuildRemoteEntriesShapeAndDedupe()
        {
            var rows = WatchProgressSyncService.BuildRemoteProgressEntries(new[]
            {
                Item("tt1", season: 1, episode: 1),
                Item("tt1", season: 1, episode: 1) // duplicate progress_key
            });

            var row = Assert.Single(rows);
            Assert.Equal("tt1", row["content_id"]);
            Assert.Equal("__nuvio_episode__:1:1", row["video_id"]);
            Assert.Equal("tt1_s1e1", row["progress_key"]);
        }

        [Fact]
        public void ProgressSync_PushSignatureIsContentStable()
        {
            var rows = WatchProgressSyncService.BuildRemoteProgressEntries(
                new[] { Item("tt1") });
            var again = WatchProgressSyncService.BuildRemoteProgressEntries(
                new[] { Item("tt1") });
            var changed = WatchProgressSyncService.BuildRemoteProgressEntries(
                new[] { Item("tt1", positionMs: 999) });

            Assert.Equal(WatchProgressSyncService.BuildPushSignature(rows),
                WatchProgressSyncService.BuildPushSignature(again));
            Assert.NotEqual(WatchProgressSyncService.BuildPushSignature(rows),
                WatchProgressSyncService.BuildPushSignature(changed));
        }

        // -----------------------------------------------------------------
        // SavedLibrarySyncService — mapping + paged pull over RecordingHandler
        // ------------------------------------------------------------------

        [Fact]
        public void SavedLibrary_NormalizePosterShapeFallsBackToPoster()
        {
            Assert.Equal("LANDSCAPE", SavedLibrarySyncService.NormalizePosterShape("landscape"));
            Assert.Equal("POSTER", SavedLibrarySyncService.NormalizePosterShape("banner"));
            Assert.Equal("POSTER", SavedLibrarySyncService.NormalizePosterShape(null));
        }

        [Fact]
        public void SavedLibrary_MapRemoteItemReadsSnakeCaseWithDefaults()
        {
            var row = JsonDocument.Parse(@"{
                ""content_id"": ""tt1"",
                ""content_type"": ""series"",
                ""name"": ""Show"",
                ""poster_shape"": ""square"",
                ""imdb_rating"": 8.7,
                ""genres"": [""drama""],
                ""added_at"": 1700000000000
            }").RootElement;

            var item = SavedLibrarySyncService.MapRemoteItem(row);

            Assert.Equal("tt1", item.ContentId);
            Assert.Equal("Show", item.Title);
            Assert.Equal("SQUARE", item.PosterShape);
            Assert.Equal(8.7, item.ImdbRating);
            Assert.Equal("drama", Assert.Single(item.Genres));
            Assert.Equal(1700000000000, item.UpdatedAt);
            Assert.Equal("Untitled", SavedLibrarySyncService.MapRemoteItem(
                JsonDocument.Parse("{}").RootElement).Title);
        }

        [Fact]
        public async Task SavedLibrary_PullPagesUntilShortPageAndReplaces()
        {
            var client = CreateClient();
            var auth = await CreateAuthenticatedAuthAsync();
            var profiles = new ProfileManager(_store);
            var service = new SavedLibrarySyncService(client, auth, profiles, _store);

            var fullPage = new object[SavedLibrarySyncService.PullPageSize];
            for (var index = 0; index < fullPage.Length; index++)
            {
                fullPage[index] = new { content_id = "tt" + index };
            }
            _handler.Enqueue(JsonArrayResponse(fullPage));
            _handler.Enqueue(JsonArrayResponse(new
            {
                content_id = "tt-last",
                title = "Last"
            }));

            var items = await service.PullAsync();

            Assert.Equal(SavedLibrarySyncService.PullPageSize + 1, items.Count);
            Assert.Contains(items, item => item.ContentId == "tt-last");
            Assert.Equal("Last",
                Assert.Single(items.Where(item => item.ContentId == "tt-last")).Title);

            // Two RPC calls were made for two pages.
            Assert.Equal(2, _handler.Requests.Count(request =>
                request.RequestUri.ToString().Contains("sync_pull_library")));
        }

        [Fact]
        public void SavedLibrary_ToRemoteItemUsesSnakeCaseWireShape()
        {
            var remote = SavedLibrarySyncService.ToRemoteItem(new SavedLibraryItem
            {
                ContentId = "tt9",
                ContentType = "movie",
                Title = "Nine",
                PosterShape = "weird",
                ImdbRating = 7.5,
                Genres = new List<string> { "sci-fi" }
            });

            Assert.Equal("tt9", remote["content_id"]);
            Assert.Equal("Nine", remote["name"]);
            Assert.Equal("POSTER", remote["poster_shape"]); // invalid input normalized
            Assert.Equal(7.5, remote["imdb_rating"]);
            Assert.Equal("sci-fi", Assert.Single((string[])remote["genres"]));
        }

        // -----------------------------------------------------------------
        // StartupSyncService — orchestration gating
        // ------------------------------------------------------------------

        [Fact]
        public async Task Startup_NotStarted_RequestSyncNowReturnsFalse()
        {
            var client = CreateClient();
            var auth = await CreateAuthenticatedAuthAsync();
            var profiles = new ProfileManager(_store);
            var service = CreateOrchestrator(client, auth, profiles, out var calls);

            Assert.False(await service.RequestSyncNowAsync());
            Assert.False(service.IsStarted);
            Assert.Empty(calls);
        }

        [Fact]
        public async Task Startup_UnauthenticatedPullReturnsFalseWithoutHttp()
        {
            _handler = new RecordingHandler();
            _httpClient = new HttpClient(_handler); // signed out
            var supabase = new SupabaseClient(_httpClient,
                new StubSessionTokenProvider("session-token", "refresh-token"));
            var signedOut = new AuthManager(_httpClient, _store);
            var profiles = new ProfileManager(_store);
            var service = new StartupSyncService(
                signedOut,
                profilesSync: new ProfileSyncService(supabase, signedOut, profiles),
                profiles: profiles,
                savedLibrary: new SavedLibrarySyncService(supabase, signedOut, profiles, _store),
                watchedItems: new WatchedItemsSyncService(supabase, signedOut, profiles, _store),
                watchProgress: new WatchProgressSyncService(supabase, signedOut, profiles,
                    _store, new StoredSyncClientIdProvider(_store),
                    () => Array.Empty<ProgressSyncItem>(), _ => { }));

            Assert.False(await service.SyncPullAsync());
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task Startup_StartedPullRunsStepsOncePerCycle()
        {
            var client = CreateClient();
            var auth = await CreateAuthenticatedAuthAsync();
            var profiles = new ProfileManager(_store);
            var service = CreateOrchestrator(client, auth, profiles, out var calls);

            // Pull sequence: profiles RPC, watched-items RPC, watch-progress RPC,
            // saved-library page RPC — then the extra pull step fires once.
            _handler.Enqueue(JsonArrayResponse()); // sync_pull_profiles
            _handler.Enqueue(JsonArrayResponse()); // sync_pull_watched_items
            _handler.Enqueue(JsonArrayResponse()); // sync_pull_watch_progress
            _handler.Enqueue(JsonArrayResponse()); // sync_pull_library page

            await service.StartAsync(runInitialPull: false);
            Assert.True(await service.RequestSyncNowAsync());
            Assert.Single(calls.Where(call => call == "extra-pull"));

            // Second cycle consumes another round of responses.
            _handler.Enqueue(JsonArrayResponse());
            _handler.Enqueue(JsonArrayResponse());
            _handler.Enqueue(JsonArrayResponse());
            _handler.Enqueue(JsonArrayResponse());
            Assert.True(await service.SyncCycleAsync());
            Assert.Equal(2, calls.Count(call => call == "extra-pull"));

            service.Stop();
            Assert.False(service.IsStarted);
        }

        private StartupSyncService CreateOrchestrator(
            SupabaseClient client,
            AuthManager auth,
            ProfileManager profiles,
            out List<string> calls)
        {
            calls = new List<string>();
            var callLog = calls;
            var savedLibrary = new SavedLibrarySyncService(client, auth, profiles, _store);
            var watchedItems = new WatchedItemsSyncService(client, auth, profiles, _store);
            var watchProgress = new WatchProgressSyncService(
                client, auth, profiles, _store,
                new StoredSyncClientIdProvider(_store),
                () => Array.Empty<ProgressSyncItem>(),
                _ => { });
            var service = new StartupSyncService(
                auth,
                profilesSync: new ProfileSyncService(client, auth, profiles),
                profiles: profiles,
                savedLibrary: savedLibrary,
                watchedItems: watchedItems,
                watchProgress: watchProgress,
                delay: _ => Task.CompletedTask);
            service.AddExtraPullStep(() =>
            {
                callLog.Add("extra-pull");
                return Task.CompletedTask;
            });
            service.AddExtraPushStep(() =>
            {
                callLog.Add("extra-push");
                return Task.CompletedTask;
            });
            return service;
        }
    }
}
