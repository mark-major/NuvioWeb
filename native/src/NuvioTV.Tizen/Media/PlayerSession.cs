using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tizen.Multimedia;

namespace NuvioTV.Tizen.Media
{
    /// <summary>
    /// Wraps Tizen.Multimedia.Player with the surface playerScreen.js expects
    /// (plan Task 15.1): prepare/start/pause/stop/seek/rate, embedded track
    /// enumeration, external subtitle path + offset, and event fan-out.
    /// Video is bound to the NUI window; overlay views sit above the video.
    /// </summary>
    public sealed class PlayerSession : IDisposable
    {
        private readonly Player _player;
        private readonly global::Tizen.NUI.Window _window;
        private bool _prepared;

        public event EventHandler<PlayerEvent> Event;

        public PlayerSession(global::Tizen.NUI.Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _player = new Player();
            _player.BufferingProgressChanged += (_, e) =>
                Raise(PlayerEventType.Buffering, e.Percent.ToString(), e.Percent);
            _player.PlaybackCompleted += (_, __) => Raise(PlayerEventType.Completed, null);
            _player.PlaybackInterrupted += (_, __) => Raise(PlayerEventType.Interrupted, null);
            _player.VideoStreamChanged += (_, __) => Raise(PlayerEventType.VideoStreamChanged, null);
            _player.ErrorOccurred += (_, e) => Raise(PlayerEventType.Error, e.Error.ToString());
        }

        public async Task<PlayerPrepareResult> PrepareAsync(
            PlayerSource src, PlayerOptions opts = null)
        {
            if (src?.Url == null) throw new ArgumentNullException(nameof(src));
            opts = opts ?? new PlayerOptions();

            _player.SetSource(new MediaUriSource(src.Url.ToString()));
            _player.Display = new Display(_window);

            if (!string.IsNullOrEmpty(src.UserAgent))
            {
                _player.UserAgent = src.UserAgent;
            }
            if (!string.IsNullOrEmpty(src.Cookie))
            {
                _player.Cookie = src.Cookie;
            }
            try
            {
                _player.BufferingTime = new PlayerBufferingTime(opts.BufferingTimeMs, 0);
            }
            catch { /* device range */ }
            ApplyAspectMode(opts.AspectMode);

            await _player.PrepareAsync();
            _prepared = true;

            var result = new PlayerPrepareResult
            {
                DurationMs = SafeDurationMs(),
                AudioTracks = EnumerateAudioTracks(),
                SubtitleTracks = EnumerateSubtitleTracks()
            };

            // External subtitles: first entry becomes the active subtitle path.
            var external = src.ExternalSubtitles?.FirstOrDefault(s => s?.Uri != null);
            if (external != null)
            {
                try
                {
                    _player.SetSubtitle(external.Uri.ToString());
                }
                catch (Exception ex)
                {
                    global::Tizen.Log.Warn("NuvioTV", "SetSubtitle failed: " + ex.Message);
                }
            }
            return result;
        }

        public Task StartAsync() { _player.Start(); return Task.CompletedTask; }
        public void Pause() => _player.Pause();
        public void Stop() => _player.Stop();

        public void Unprepare()
        {
            if (_prepared)
            {
                try { _player.Unprepare(); } catch { /* already unprepared */ }
                _prepared = false;
            }
        }

        public async Task SeekAsync(long positionMs)
        {
            await _player.SetPlayPositionAsync((int)Math.Max(0, positionMs), true);
        }

        public long GetPositionMs() => _player.GetPlayPosition();

        public long GetDurationMs() => SafeDurationMs();

        public float PlaybackRate
        {
            get { return _playbackRate; }
            set
            {
                _playbackRate = value;
                try { _player.SetPlaybackRate(value); }
                catch (Exception ex)
                {
                    global::Tizen.Log.Warn("NuvioTV", "rate unsupported: " + ex.Message);
                }
            }
        }
        private float _playbackRate = 1f;

        public IReadOnlyList<PlayerAudioTrack> AudioTracks => EnumerateAudioTracks();

        /// <summary>Embedded audio track switching is not exposed by API7 MediaPlayer; logged no-op.</summary>
        public Task SelectAudioTrackAsync(int index)
        {
            global::Tizen.Log.Warn("NuvioTV", $"audio track selection unavailable (index {index})");
            return Task.CompletedTask;
        }

        public IReadOnlyList<PlayerSubtitleTrack> SubtitleTracks =>
            EnumerateSubtitleTracks();

        public Task SelectSubtitleTrackAsync(int index)
        {
            global::Tizen.Log.Warn("NuvioTV", $"subtitle track selection unavailable (index {index})");
            return Task.CompletedTask;
        }

        public void SetExternalSubtitle(Uri uri, long offsetMs)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            _player.SetSubtitle(uri.ToString());
            if (offsetMs != 0)
            {
                _player.SetSubtitleOffset((int)offsetMs);
            }
        }

        public PlayerDisplaySettings DisplaySettings
        {
            set { _player.DisplaySettings.Mode = value.Mode; }
        }

        private void ApplyAspectMode(string mode)
        {
            try
            {
                switch ((mode ?? "original").ToLowerInvariant())
                {
                    case "16x9":
                        _player.DisplaySettings.Mode = PlayerDisplayMode.CroppedFull;
                        break;
                    case "4x3":
                        _player.DisplaySettings.Mode = PlayerDisplayMode.Roi;
                        break;
                    case "full":
                        _player.DisplaySettings.Mode = PlayerDisplayMode.FullScreen;
                        break;
                    default:
                        _player.DisplaySettings.Mode = PlayerDisplayMode.LetterBox;
                        break;
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "aspect mode failed: " + ex.Message);
            }
        }

        private long SafeDurationMs()
        {
            try
            {
                return _player.StreamInfo?.GetDuration() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private IReadOnlyList<PlayerAudioTrack> EnumerateAudioTracks()
        {
            var list = new List<PlayerAudioTrack>();
            try
            {
                var info = _player.AudioTrackInfo;
                var count = info?.GetCount() ?? 0;
                for (var i = 0; i < count; i++)
                {
                    string language = null;
                    try { language = info.GetLanguageCode(i); } catch { /* optional */ }
                    list.Add(new PlayerAudioTrack
                    {
                        Index = i,
                        Language = language,
                        CodecLabel = Core.Media.AudioTrackCodecMetadata.LabelFor(null)
                    });
                }
            }
            catch { /* enumeration unsupported on this source */ }
            return list;
        }

        private IReadOnlyList<PlayerSubtitleTrack> EnumerateSubtitleTracks()
        {
            var list = new List<PlayerSubtitleTrack>();
            try
            {
                var info = _player.SubtitleTrackInfo;
                var count = info?.GetCount() ?? 0;
                for (var i = 0; i < count; i++)
                {
                    string language = null;
                    try { language = info.GetLanguageCode(i); } catch { /* optional */ }
                    list.Add(new PlayerSubtitleTrack { Index = i, Language = language });
                }
            }
            catch { /* enumeration unsupported on this source */ }
            return list;
        }

        private void Raise(PlayerEventType type, string detail, int progress = 0) =>
            Event?.Invoke(this, new PlayerEvent { Type = type, Detail = detail, ProgressPercent = progress });

        public void Dispose()
        {
            Unprepare();
            _player.Dispose();
        }
    }
}
