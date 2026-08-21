using System;
using System.Threading;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Bottom toast (toast parity): dark pill, auto-dismiss after the given
    /// delay with a 150ms fade-out.
    /// </summary>
    public sealed class Toast : View
    {
        private readonly TextLabel _label = new TextLabel();
        private System.Threading.Timer _timer;

        public Toast()
        {
            WidthResizePolicy = ResizePolicyType.UseNaturalSize;
            HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            PositionUsesPivotPoint = true;
            PivotPoint = new Vector3(0.5f, 1f, 0.5f);
            ParentOrigin = new Vector3(0.5f, 1f, 0.5f);
            Position = new Position(0, -80);
            BackgroundColor = new Color(0f, 0f, 0f, 0.85f);
            CornerRadius = 30f;
            Padding = new Extents(40, 40, 24, 24);

            _label.PointSize = NuiFoundation.DesignTokens.TypeBody;
            _label.TextColor = Color.White;
            _label.HorizontalAlignment = HorizontalAlignment.Center;
            _label.VerticalAlignment = VerticalAlignment.Center;
            Add(_label);
        }

        public void Show(string message, int durationMs = 2500)
        {
            _label.Text = message ?? "";
            Opacity = 1f;
            _timer?.Dispose();
            _timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var fade = new Animation(150);
                    fade.AnimateTo(this, "Opacity", 0f);
                    fade.Play();
                }
                catch
                {
                    Opacity = 0f;
                }
            }, null, durationMs, Timeout.Infinite);
        }

        protected override void Dispose(DisposeTypes type)
        {
            _timer?.Dispose();
            base.Dispose(type);
        }
    }
}
