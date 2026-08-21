using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of consoleDebugScreen.js: read-only view over the DebugLogBuffer
    /// ring (300 events); Up/Down scrolls, Back exits.
    /// </summary>
    public sealed class DebugConsoleScreen : ScreenBase
    {
        private readonly Core.Diagnostics.DebugLogBuffer _buffer =
            new Core.Diagnostics.DebugLogBuffer();

        private readonly TextLabel _output = new TextLabel();
        private int _scrollOffset;

        public DebugConsoleScreen()
        {
            var heading = new TextLabel
            {
                Text = "Debug Console",
                PointSize = DesignTokens.TypeSubtitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 40),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _output.PointSize = 14f;
            _output.TextColor = ThemeManager.Current.TextSecondaryColor;
            _output.MultiLine = true;
            _output.Position = new Position(DesignTokens.SafeGutter, 110);
            _output.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 900);
            _output.HorizontalAlignment = HorizontalAlignment.Begin;
            _output.VerticalAlignment = VerticalAlignment.Top;
            Add(_output);
        }

        /// <summary>Host app feeds its shared buffer here before mounting.</summary>
        public void Bind(Core.Diagnostics.DebugLogBuffer buffer)
        {
            _buffer.Clear();
            foreach (var line in buffer.Snapshot())
            {
                _buffer.Info(line);
            }
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            Render();
            return Task.CompletedTask;
        }

        private void Render()
        {
            var lines = _buffer.Snapshot();
            var visibleCount = Math.Min(lines.Count - _scrollOffset, 60);
            var text = new List<string>();
            for (var i = Math.Max(0, _scrollOffset); i < lines.Count && text.Count < visibleCount; i++)
            {
                text.Add(lines[i]);
            }
            _output.Text = text.Count > 0 ? string.Join("\n", text) : "No events captured.";
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up when _scrollOffset > 0:
                    _scrollOffset--;
                    Render();
                    return true;
                case NuvioKey.Down when _scrollOffset < Math.Max(0, _buffer.Count - 1):
                    _scrollOffset++;
                    Render();
                    return true;
            }
            return false;
        }

        public override object ConsumeBackRequest() => null;
    }
}
