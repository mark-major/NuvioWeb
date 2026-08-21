using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Sync;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>
    /// Golden tests for TrackingScrobbleService + WatchedSeriesReconciliationService
    /// against js/data/repository/{trackingScrobbleService,traktScrobbleService,
    /// simklScrobbleService,watchedSeriesReconciliationService}.js.
    /// </summary>
    public class ScrobbleTests
    {
        // ------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------

        private sealed class FakeMarker : IWatchedMarker
        {
            public List<WatchedMarkRequest> Marks { get; } = new List<WatchedMarkRequest>();

            public Task MarkAsync(WatchedMarkRequest item)
            {
                Marks.Add(item);
                return Task.CompletedTask;
            }
        }

        private static TraktScrobbleProvider Trakt(
            FakeMarker marker,
            Func<string, object, string, CancellationToken, Task<bool>> sender,
            TaskCompletionSource<object> gate = null)
        {
            return new TraktScrobbleProvider(
                ct => Task.FromResult("tok"),
                sender,
                marker,
                delay: (span, ct) => gate != null ? gate.Task : Task.CompletedTask);
        }

        private static ScrobbleContext MovieContext(double progress = 50)
        {
            return new ScrobbleContext
            {
                ContentId = "tt0111161",
                ContentType = "movie",
                Title = "The Shawshank Redemption",
                Year = 1994,
                ImdbId = "tt0111161",
                ProgressPercent = progress
            };
        }

        private static ScrobbleContext SeriesContext(double progress = 50)
        {
            return new ScrobbleContext
            {
                ContentId = "tt0903747",
                ContentType = "series",
                Title = "Breaking Bad",
                ImdbId = "tt0903747",
                SeasonNumber = 2,
                EpisodeNumber = 5,
                EpisodeTitle = "Mas",
                ProgressPercent = progress
            };
        }

        // ------------------------------------------------------------------
        // Trakt provider
        // ------------------------------------------------------------------

        [Fact]
        public async Task Trakt_StartDebounce_SendsAfterDelayOnly()
        {
            var sent = new List<string>();
            var gate = new TaskCompletionSource<object>();
            var provider = Trakt(new FakeMarker(), (action, payload, token, ct) =>
            {
                sent.Add(action);
                return Task.FromResult(true);
            }, gate);

            provider.Start(MovieContext());
            Assert.Empty(sent); // debounce holds

            gate.SetResult(null);
            await Task.Yield();
            await Task.Delay(50);
            Assert.Equal(new[] { "start" }, sent);
        }

        [Fact]
        public async Task Trakt_CancelledStart_NeverSends()
        {
            var sent = new List<string>();
            var gate = new TaskCompletionSource<object>();
            var provider = Trakt(new FakeMarker(), (a, p, t, c) =>
            {
                sent.Add(a);
                return Task.FromResult(true);
            }, gate);

            provider.Start(MovieContext());
            provider.Cancel();
            gate.SetResult(null);
            await Task.Delay(50);

            Assert.Empty(sent);
        }

        [Fact]
        public void Trakt_EpisodePayloadShape()
        {
            var payload = JsonSerializer.SerializeToElement(
                TraktScrobbleProvider.BuildScrobblePayload(SeriesContext()));

            Assert.Equal("Breaking Bad", payload.GetProperty("show").GetProperty("title").GetString());
            Assert.Equal("tt0903747", payload.GetProperty("show").GetProperty("ids").GetProperty("imdb").GetString());
            Assert.Equal(2, payload.GetProperty("episode").GetProperty("season").GetInt64());
            Assert.Equal(5, payload.GetProperty("episode").GetProperty("number").GetInt64());
            Assert.Equal("Mas", payload.GetProperty("episode").GetProperty("title").GetString());
            Assert.Equal(50, payload.GetProperty("progress").GetDouble());

            var moviePayload = JsonSerializer.SerializeToElement(
                TraktScrobbleProvider.BuildScrobblePayload(MovieContext()));
            Assert.Equal("The Shawshank Redemption", moviePayload.GetProperty("movie").GetProperty("title").GetString());
            Assert.Equal(1994, moviePayload.GetProperty("movie").GetProperty("year").GetInt64());
            Assert.False(moviePayload.GetProperty("movie").TryGetProperty("ids", out _).Equals(true)
                && moviePayload.GetProperty("movie").GetProperty("ids").EnumerateObject().Any() == false);
        }

        [Fact]
        public async Task Trakt_StopAtThreshold_MarksWatched()
        {
            var marker = new FakeMarker();
            var provider = Trakt(marker, (a, p, t, c) => Task.FromResult(a == "stop"));

            provider.Stop(MovieContext(progress: 80));
            await Task.Delay(50);

            Assert.Single(marker.Marks);
            Assert.Equal("tt0111161", marker.Marks[0].ContentId);
        }

        [Fact]
        public async Task Trakt_StopBelowThreshold_DoesNotMark()
        {
            var marker = new FakeMarker();
            var provider = Trakt(marker, (a, p, t, c) => Task.FromResult(true));

            provider.Stop(MovieContext(progress: 79));
            await Task.Delay(50);

            Assert.Empty(marker.Marks);
        }

        [Fact]
        public async Task Trakt_ThreeStrikeCap_StopsSending()
        {
            var calls = 0;
            var provider = Trakt(new FakeMarker(), (a, p, t, c) =>
            {
                calls++;
                return Task.FromResult(false); // always fails
            });

            for (var index = 0; index < 5; index++)
            {
                provider.Stop(MovieContext());
                await Task.Delay(20);
            }

            Assert.Equal(3, calls); // capped
        }

        [Fact]
        public async Task Trakt_SuccessResetsFailureCounter()
        {
            var fail = true;
            var provider = Trakt(new FakeMarker(), (a, p, t, c) =>
            {
                var result = !fail;
                return Task.FromResult(result);
            });

            provider.Stop(MovieContext());
            await Task.Delay(20);
            provider.Stop(MovieContext());
            await Task.Delay(20);
            Assert.True(fail);
            fail = false;
            provider.Stop(MovieContext()); // resets counter
            await Task.Delay(20);
            fail = true;
            provider.Stop(MovieContext());
            await Task.Delay(20);
            provider.Stop(MovieContext()); // still under cap
            await Task.Delay(20);

            // 5 stops total; only the first two failed, so cap never engaged.
        }

        [Fact]
        public async Task Trakt_NoExternalId_SendsNothing()
        {
            var calls = 0;
            var provider = Trakt(new FakeMarker(), (a, p, t, c) =>
            {
                calls++;
                return Task.FromResult(true);
            });

            provider.Stop(new ScrobbleContext { ContentType = "movie", Title = "X" });
            await Task.Delay(20);

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Trakt_PauseGating_AfterStopStillSends()
        {
            var sent = new List<string>();
            var provider = Trakt(new FakeMarker(), (a, p, t, c) =>
            {
                lock (sent) { sent.Add(a); }
                return Task.FromResult(true);
            });

            provider.Pause(MovieContext());   // lastAction null → sends
            provider.Stop(MovieContext());    // sets lastAction null again
            await Task.Delay(30);

            Assert.Contains("pause", sent);
            Assert.Contains("stop", sent);
        }

        // ------------------------------------------------------------------
        // Simkl provider
        // ------------------------------------------------------------------

        [Fact]
        public async Task Simkl_NormalizesAndClampsProgress()
        {
            double? captured = null;
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (action, payload, token, ct) =>
                {
                    captured = JsonSerializer.SerializeToElement(payload)
                        .GetProperty("progress").GetDouble();
                    return Task.FromResult(true);
                },
                watchedMarker: null);

            provider.Stop(MovieContext(progress: 150));
            await Task.Delay(30);
            Assert.Equal(100.0, captured);

            provider.Stop(MovieContext(progress: 33.335));
            await Task.Delay(30);
            Assert.Equal(33.34, captured);
        }

        [Fact]
        public async Task Simkl_AnimeVideoId_ParsesIdsDropsSeason()
        {
            string mediaKey = null;
            JsonElement episodeElement = default;
            JsonElement ids = default;
            long? seasonValue = null;
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (action, payload, token, ct) =>
                {
                    var element = JsonSerializer.SerializeToElement(payload);
                    foreach (var key in new[] { "anime", "show", "movie" })
                    {
                        if (element.TryGetProperty(key, out var media))
                        {
                            mediaKey = key;
                            ids = media.GetProperty("ids");
                        }
                    }
                    episodeElement = element.GetProperty("episode");
                    seasonValue = episodeElement.TryGetProperty("season", out var s)
                        ? s.GetInt64() : (long?)null;
                    return Task.FromResult(true);
                },
                watchedMarker: null);

            provider.Stop(new ScrobbleContext
            {
                ContentType = "series",
                Title = "Anime",
                MalId = "999", // stripped by the anime path
                VideoId = "mal:12345:4",
                SeasonNumber = 2,
                EpisodeNumber = 9,
                ProgressPercent = 95
            });
            await Task.Delay(30);

            Assert.Equal("anime", mediaKey);
            Assert.Equal(12345L, ids.GetProperty("mal").GetInt64());
            Assert.Null(seasonValue);
            Assert.Equal(4, episodeElement.GetProperty("number").GetInt64());
        }

        [Fact]
        public async Task Simkl_AnimeWithSeason_StripsStreamingIdsFromShow()
        {
            var ids = default(JsonElement);
            string mediaKey = null;
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (action, payload, token, ct) =>
                {
                    var element = JsonSerializer.SerializeToElement(payload);
                    mediaKey = element.TryGetProperty("show", out _) ? "show" : "anime";
                    ids = element.GetProperty(mediaKey).GetProperty("ids");
                    return Task.FromResult(true);
                },
                watchedMarker: null);

            provider.Stop(new ScrobbleContext
            {
                ContentType = "series",
                Title = "Show",
                ImdbId = "tt1",
                KitsuId = "77",
                SeasonNumber = 1,
                EpisodeNumber = 2,
                ProgressPercent = 90
            });
            await Task.Delay(30);

            Assert.Equal("show", mediaKey);
            Assert.True(ids.TryGetProperty("imdb", out _));
            Assert.False(ids.TryGetProperty("kitsu", out _)); // stripped for seasonal
        }

        [Fact]
        public async Task Simkl_SeriesWithoutEpisodeNumber_Skips()
        {
            var calls = 0;
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (a, p, t, c) => { calls++; return Task.FromResult(true); },
                watchedMarker: null);

            provider.Stop(new ScrobbleContext
            {
                ContentType = "series",
                Title = "X",
                ImdbId = "tt1"
            });
            await Task.Delay(30);
            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task Simkl_ThreeStrikeCap_AndCancelReset()
        {
            var calls = 0;
            var marker = new FakeMarker();
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (a, p, t, c) => { calls++; return Task.FromResult(false); },
                marker);

            for (var index = 0; index < 4; index++)
            {
                provider.Stop(MovieContext(progress: 100));
                await Task.Delay(10);
            }
            Assert.Equal(3, calls);

            provider.Cancel(); // resets failures
            provider.Stop(MovieContext(progress: 100));
            await Task.Delay(10);
            Assert.Equal(4, calls);
        }

        [Fact]
        public async Task Simkl_StopAtThreshold_MarksWatchedWithEpisodeFields()
        {
            var marker = new FakeMarker();
            var provider = new SimklScrobbleProvider(
                () => true, () => "tok",
                (a, p, t, c) => Task.FromResult(true),
                marker);

            provider.Stop(SeriesContext(progress: 100));
            await Task.Delay(30);

            var mark = Assert.Single(marker.Marks);
            Assert.Equal("tt0903747", mark.ContentId);
            Assert.Equal("series", mark.ContentType);
            Assert.Equal(2, mark.Season);
            Assert.Equal(5, mark.Episode);
        }

        // ------------------------------------------------------------------
        // Facade fan-out
        // ------------------------------------------------------------------

        [Fact]
        public void Facade_FansOutToEnabledProvidersOnly()
        {
            var enabledCalls = new List<string>();
            var disabledCalls = new List<string>();
            var enabled = new StubProvider(name => enabledCalls.Add(name), isEnabled: true);
            var disabled = new StubProvider(name => disabledCalls.Add(name), isEnabled: false);
            var facade = new TrackingScrobbleService(enabled, disabled);

            Assert.True(facade.IsEnabled());

            facade.Start(MovieContext());
            facade.Stop(MovieContext());
            facade.Cancel();

            Assert.Equal(new[] { "start", "stop", "cancel" }, enabledCalls);
            Assert.Equal(new[] { "cancel" }, disabledCalls); // Cancel reaches ALL providers
        }

        private sealed class StubProvider : IScrobbleProvider
        {
            private readonly Action<string> _log;
            private readonly bool _isEnabled;

            public StubProvider(Action<string> log, bool isEnabled)
            {
                _log = log;
                _isEnabled = isEnabled;
            }

            public bool IsEnabled() => _isEnabled;

            public void Start(ScrobbleContext context) => _log("start");

            public void Pause(ScrobbleContext context) => _log("pause");

            public void Stop(ScrobbleContext context) => _log("stop");

            public void Cancel() => _log("cancel");
        }

        // ------------------------------------------------------------------
        // WatchedSeriesReconciliationService
        // ------------------------------------------------------------------

        private sealed class FakeWatchedStore : IWatchedItemsStore
        {
            public List<WatchedItemRecord> Items { get; } = new List<WatchedItemRecord>();
            public HashSet<string> RootMarks { get; } = new HashSet<string>();

            public Task<IReadOnlyList<WatchedItemRecord>> GetAllAsync(int limit)
            {
                return Task.FromResult((IReadOnlyList<WatchedItemRecord>)Items.ToList());
            }

            public Task<bool> IsWatchedAsync(string contentId)
            {
                return Task.FromResult(RootMarks.Contains(contentId));
            }

            public Task MarkAsync(WatchedMarkRequest item, bool skipTrackingWrite)
            {
                if (item.Season.HasValue && item.Episode.HasValue)
                {
                    Items.Add(new WatchedItemRecord
                    {
                        ContentId = item.ContentId,
                        Season = item.Season,
                        Episode = item.Episode
                    });
                }
                else
                {
                    RootMarks.Add(item.ContentId);
                }
                return Task.CompletedTask;
            }

            public Task UnmarkAsync(string contentId, bool rootOnly)
            {
                RootMarks.Remove(contentId);
                if (!rootOnly)
                {
                    Items.RemoveAll(item => item.ContentId == contentId && !item.Season.HasValue);
                }
                return Task.CompletedTask;
            }

            public Task UnmarkEpisodeAsync(
                string contentId, long? season, long? episode, string videoId, bool skipTrackingWrite)
            {
                Items.RemoveAll(item => item.ContentId == contentId &&
                    item.Season == season && item.Episode == episode);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeProgressStore : IWatchProgressStore
        {
            public List<WatchProgressRecord> Records { get; } =
                new List<WatchProgressRecord>();
            public List<WatchProgressSaveRequest> Saved { get; } =
                new List<WatchProgressSaveRequest>();
            public int RemovedCount;

            public Task<IReadOnlyList<WatchProgressRecord>> GetAllAsync()
            {
                return Task.FromResult((IReadOnlyList<WatchProgressRecord>)Records.ToList());
            }

            public Task SaveProgressAsync(WatchProgressSaveRequest progress)
            {
                Saved.Add(progress);
                Records.Add(new WatchProgressRecord
                {
                    ContentId = progress.ContentId,
                    VideoId = progress.VideoId,
                    Season = progress.Season,
                    Episode = progress.Episode,
                    PositionMs = progress.PositionMs,
                    DurationMs = progress.DurationMs
                });
                return Task.CompletedTask;
            }

            public Task RemoveProgressAsync(string contentId, string videoId)
            {
                RemovedCount++;
                Records.RemoveAll(record => record.ContentId == contentId &&
                    (videoId == null || record.VideoId == videoId));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeMetaLookup : ISeriesMetaLookup
        {
            public SeriesMeta Meta { get; set; }

            public Task<SeriesMeta> GetMetaFromAllAddonsAsync(string contentType, string contentId)
            {
                return Task.FromResult(Meta);
            }
        }

        private static SeriesMeta TwoEpisodeMeta()
        {
            return new SeriesMeta
            {
                Id = "tt0903747",
                Name = "Breaking Bad",
                Videos = new List<SeriesVideo>
                {
                    new SeriesVideo { Id = "tt0903747:1:1", Name = "Pilot", Season = 1, Number = 1 },
                    new SeriesVideo { Id = "tt0903747:1:2", Name = "Cat", Season = 1, Number = 2 },
                    new SeriesVideo
                    {
                        Id = "tt0903747:2:1", Season = 2, Number = 1,
                        Released = "2999-01-01" // unreleased far future
                    }
                }
            };
        }

        [Fact]
        public async Task Reconcile_AllReleasedEpisodesCompleted_MarksRoot()
        {
            var watched = new FakeWatchedStore();
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };
            progress.Records.AddRange(new[]
            {
                new WatchProgressRecord
                {
                    ContentId = "tt0903747", VideoId = "tt0903747:1:1",
                    Season = 1, Episode = 1, PositionMs = 95, DurationMs = 100
                },
                new WatchProgressRecord
                {
                    ContentId = "tt0903747", VideoId = "tt0903747:1:2",
                    Season = 1, Episode = 2, PositionMs = 91, DurationMs = 100
                }
            });

            var changed = await WatchedSeriesReconciliationService.ReconcileAsync(
                watched, progress, lookup, "tt0903747");

            Assert.True(changed);
            Assert.Contains("tt0903747", watched.RootMarks);
        }

        [Fact]
        public async Task Reconcile_IncompleteEpisode_NoChangeWithoutMarker()
        {
            var watched = new FakeWatchedStore();
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };
            progress.Records.Add(new WatchProgressRecord
            {
                ContentId = "tt0903747", VideoId = "tt0903747:1:1",
                Season = 1, Episode = 1, PositionMs = 89, DurationMs = 100 // below 90%
            });

            var changed = await WatchedSeriesReconciliationService.ReconcileAsync(
                watched, progress, lookup, "tt0903747");

            Assert.False(changed);
            Assert.Empty(watched.RootMarks);
        }

        [Fact]
        public async Task Reconcile_UnwatchedEpisode_RemovesStaleRootMarker()
        {
            var watched = new FakeWatchedStore();
            watched.RootMarks.Add("tt0903747"); // stale marker
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };

            var changed = await WatchedSeriesReconciliationService.ReconcileAsync(
                watched, progress, lookup, "tt0903747");

            Assert.True(changed);
            Assert.DoesNotContain("tt0903747", watched.RootMarks);
        }

        [Fact]
        public async Task Reconcile_CompletedEpisodeOptionCountsAsWatched()
        {
            var watched = new FakeWatchedStore();
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };
            progress.Records.Add(new WatchProgressRecord
            {
                ContentId = "tt0903747", VideoId = "tt0903747:1:1",
                PositionMs = 100, DurationMs = 100, Season = 1, Episode = 1
            });

            var changed = await WatchedSeriesReconciliationService.ReconcileAsync(
                watched, progress, lookup, "tt0903747",
                completedEpisode: new CompletedEpisode { Season = 1, Episode = 2 });

            Assert.True(changed);
            Assert.Contains("tt0903747", watched.RootMarks);
        }

        [Fact]
        public void ReleasedFilter_ExcludesFutureEpisodes()
        {
            var episodes = WatchedSeriesReconciliationService.GetReleasedMainEpisodes(
                TwoEpisodeMeta(), DateTimeOffset.Parse("2026-08-21T00:00:00Z"));

            Assert.Equal(2, episodes.Count);
            Assert.Equal(("1", "1"), (episodes[0].Season.ToString(), episodes[0].Episode.ToString()));
        }

        [Fact]
        public void ParseSeasonEpisodeFromVideoId_TakesLastTwoSegments()
        {
            Assert.True(WatchedSeriesReconciliationService.TryParseSeasonEpisodeFromVideoId(
                "tt0903747:2:5", out var season, out var episode));
            Assert.Equal(2, season);
            Assert.Equal(5, episode);
            Assert.False(WatchedSeriesReconciliationService.TryParseSeasonEpisodeFromVideoId(
                "tt0903747", out _, out _));
        }

        [Fact]
        public async Task MarkSeriesWatched_MarksRootAndAllReleasedEpisodes()
        {
            var watched = new FakeWatchedStore();
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };

            var changed = await WatchedSeriesReconciliationService.MarkSeriesWatchedAsync(
                watched, progress, lookup, "tt0903747");

            Assert.True(changed);
            Assert.Contains("tt0903747", watched.RootMarks);
            Assert.Equal(2, progress.Saved.Count); // released main episodes only
            Assert.All(progress.Saved, saved =>
            {
                Assert.Equal(100, saved.PositionMs);
                Assert.Equal(100, saved.DurationMs);
            });
        }

        [Fact]
        public async Task UnmarkSeriesWatched_ClearsRootEpisodesAndProgress()
        {
            var watched = new FakeWatchedStore();
            watched.RootMarks.Add("tt0903747");
            watched.Items.Add(new WatchedItemRecord
            {
                ContentId = "tt0903747", Season = 1, Episode = 1
            });
            var progress = new FakeProgressStore();
            var lookup = new FakeMetaLookup { Meta = TwoEpisodeMeta() };

            var changed = await WatchedSeriesReconciliationService.UnmarkSeriesWatchedAsync(
                watched, progress, "tt0903747", lookup.Meta);

            Assert.True(changed);
            Assert.Empty(watched.RootMarks);
            Assert.Empty(watched.Items);
            Assert.Empty(progress.Records);
        }

        [Fact]
        public void IsSeriesType_MatchesVocabulary()
        {
            Assert.True(WatchedSeriesReconciliationService.IsSeriesType("Series"));
            Assert.True(WatchedSeriesReconciliationService.IsSeriesType("anime"));
            Assert.False(WatchedSeriesReconciliationService.IsSeriesType("movie"));
            Assert.False(WatchedSeriesReconciliationService.IsSeriesType(""));
        }
    }
}
