using System;
using System.Collections.Generic;
using NuvioTV.Core.Media;

namespace NuvioTV.Tizen.Media.Subtitles
{
    /// <summary>
    /// Text subtitle overlay (Task 15.3): a safe-area TextLabel above the video
    /// layer, driven by position polling (250ms cadence from PlayerScreen) and
    /// CueLayout placement. Native player tracks take precedence when active;
    /// this renderer handles external SRT/VTT/ASS files.
    /// </summary>
    public sealed class TextSubtitleRenderer : IDisposable
    {
        private readonly global::Tizen.NUI.BaseComponents.TextLabel _label =
            new global::Tizen.NUI.BaseComponents.TextLabel();

        private IReadOnlyList<SubtitleCue> _cues = Array.Empty<SubtitleCue>();
        private int _verticalOffset;

        public TextSubtitleRenderer(global::Tizen.NUI.BaseComponents.View host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            _label.PointSize = NuiFoundation.DesignTokens.TypeBody + 8;
            _label.TextColor = global::Tizen.NUI.Color.White;
            _label.MultiLine = true;
            _label.HorizontalAlignment = global::Tizen.NUI.HorizontalAlignment.Center;
            _label.VerticalAlignment = global::Tizen.NUI.VerticalAlignment.Bottom;
            _label.Position = new global::Tizen.NUI.Position(0, -160);
            _label.WidthResizePolicy = global::Tizen.NUI.ResizePolicyType.FillToParent;
            _label.HeightResizePolicy = global::Tizen.NUI.ResizePolicyType.UseNaturalSize;
            _label.Hide();
            host.Add(_label);
        }

        public void SetCues(IReadOnlyList<SubtitleCue> cues)
        {
            _cues = cues ?? Array.Empty<SubtitleCue>();
        }

        public void Clear() => SetCues(null);

        public int VerticalOffset
        {
            get { return _verticalOffset; }
            set
            {
                _verticalOffset = Core.Media.CueLayout.ClampVerticalOffset(value);
                ApplyVerticalPlacement(new Core.Media.CueLayout.Layout
                { Vertical = "bottom", Align = "center" });
            }
        }

        /// <summary>Renders whatever is active at the given playback position.</summary>
        public void RenderAt(long positionMs)
        {
            var active = Core.Media.CueLayout.ActiveAt(_cues, positionMs);
            if (active.Count == 0)
            {
                _label.Hide();
                return;
            }

            var cue = active[active.Count - 1];
            var layout = cue.Alignment != 0
                ? Core.Media.CueLayout.FromAlignment(cue.Alignment)
                : new Core.Media.CueLayout.Layout { Vertical = "bottom", Align = "center" };

            _label.Text = cue.Text;
            ApplyVerticalPlacement(layout);
            _label.Show();
        }

        private void ApplyVerticalPlacement(Core.Media.CueLayout.Layout layout)
        {
            switch (layout.Vertical)
            {
                case "top":
                    _label.PositionY = 120 + _verticalOffset;
                    break;
                case "middle":
                    _label.PositionY = 440 + _verticalOffset;
                    break;
                default:
                    _label.PositionY = -160 + _verticalOffset; // bottom-anchored via pivot
                    break;
            }
        }

        public void Dispose()
        {
            var parent = _label.GetParent();
            parent?.Remove(_label);
            _label.Dispose();
        }
    }
}
