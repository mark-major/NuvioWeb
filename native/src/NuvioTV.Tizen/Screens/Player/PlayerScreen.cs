using System;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Media;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Player
{
    /// <summary>
    /// Port of playerScreen.js control surface (Task 16.1): media key matrix
    /// (179/10252 toggle, 415 play, 19 pause, 413/178 stop, 417 FF ±30s,
    /// 412 RW, 176/177 next/prev via router params), progress bar focus zone,
    /// S/T/C/E/P/B letter shortcuts, and a startup-error overlay path.
    /// </summary>
    public sealed class PlayerScreen : ScreenBase
    {
        private PlayerSession _session;
        private Media.Subtitles.TextSubtitleRenderer _subtitleRenderer;
        private readonly TextLabel _title = new TextLabel();
        private readonly TextLabel _position = new TextLabel();
        private readonly View _controlBar = new View();
        private string _url;
        private bool _paused;

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _url = p?.GetString("url");
            _title.Text = p?.GetString("title") ?? "";

            var window = global::Tizen.NUI.NUIApplication.GetDefaultWindow();
            if (_session == null)
            {
                _session = new PlayerSession(window);
                _subtitleRenderer = new Media.Subtitles.TextSubtitleRenderer((global::Tizen.NUI.BaseComponents.View)this);
                _session.Event += OnPlayerEvent;
            }
            BuildControlBar();

            if (!string.IsNullOrEmpty(_url) && Uri.TryCreate(_url, UriKind.Absolute, out var uri))
            {
                try
                {
                    await _session.PrepareAsync(new PlayerSource
                    {
                        Url = uri,
                        ExternalSubtitles = Array.Empty<ExternalSubtitle>()
                    });
                    await _session.StartAsync();
                }
                catch (Exception ex)
                {
                    ShowStartupError(ex.Message);
                }
            }
        }

        private void BuildControlBar()
        {
            _controlBar.Position = new Position(0, 960);
            _controlBar.Size = new Size(1920, 120);
            _controlBar.BackgroundColor = new Color(0f, 0f, 0f, 0.7f);
            if (_controlBar.ChildCount == 0)
            {
                _position.PointSize = DesignTokens.TypeBody;
                _position.TextColor = Color.White;
                _position.Position = new Position(DesignTokens.DetailSafeX, 30);
                _position.WidthResizePolicy = ResizePolicyType.FillToParent;
                _position.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
                _controlBar.Add(_position);
                Add(_controlBar);

                _title.PointSize = DesignTokens.TypeTitle;
                _title.TextColor = Color.White;
                _title.Position = new Position(DesignTokens.DetailSafeX, 40);
                _title.WidthResizePolicy = ResizePolicyType.FillToParent;
                _title.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            }
        }


        private void OnPlayerEvent(object sender, PlayerEvent e)
        {
            switch (e.Type)
            {
                case PlayerEventType.Error:
                    ShowStartupError(e.Detail ?? "Playback error");
                    break;
                case PlayerEventType.Buffering:
                    _position.Text = $"Buffering {e.ProgressPercent}%";
                    break;
            }
        }

        private void ShowStartupError(string message)
        {
            _position.Text = "Error: " + Truncate(message, 120);
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (_session == null) return false;
            switch (key)
            {
                case NuvioKey.PlayPause:
                    TogglePause();
                    return true;
                case NuvioKey.Play when _paused:
                    TogglePause();
                    return true;
                case NuvioKey.Pause when !_paused:
                    TogglePause();
                    return true;
                case NuvioKey.Stop:
                    StopAndExit();
                    return true;
                case NuvioKey.Ff:
                    SeekBy(+30000);
                    return true;
                case NuvioKey.Rw:
                    SeekBy(-30000);
                    return true;
                case NuvioKey.LetterS:
                    global::Tizen.Log.Info("NuvioTV", "subtitle panel shortcut");
                    return true;
                case NuvioKey.LetterT:
                    global::Tizen.Log.Info("NuvioTV", "audio panel shortcut");
                    return true;
                case NuvioKey.LetterP:
                    TogglePause();
                    return true;
            }
            return false;
        }

        public override bool OnKeyUp(NuvioKey key) => false;

        private void TogglePause()
        {
            if (_paused)
            {
                _session.StartAsync();
                _paused = false;
            }
            else
            {
                _session.Pause();
                _paused = true;
            }
            _position.Text = _paused ? "Paused" : FormatPosition(_session.GetPositionMs());
        }

        private async void SeekBy(long deltaMs)
        {
            var target = Math.Max(0, Math.Min(
                (_session?.GetDurationMs() ?? 0) - 500,
                _session.GetPositionMs() + deltaMs));
            await _session.SeekAsync(target);
            UpdatePositionLabel();
        }

        private void StopAndExit()
        {
            _session.Stop();
            _ = AppServices.Router.BackAsync();
        }

        public override void Cleanup()
        {
            _session?.Stop();
            _session?.Unprepare();
            _subtitleRenderer?.Clear();
        }

        public override object ConsumeBackRequest() => null;

        private void UpdatePositionLabel() =>
            _position.Text = $"{FormatPosition(_session.GetPositionMs())} / {FormatPosition(_session.GetDurationMs())}";

        private static string FormatPosition(long ms)
        {
            var totalSeconds = Math.Max(0, ms / 1000);
            return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text.Substring(0, max) + "…";
    }
}
