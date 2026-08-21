using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Full-screen loading surface (loadingIndicator.js parity): logo wordmark
    /// plus a 12-spoke spinner ring rotating about the center.
    /// </summary>
    public sealed class LoadingIndicator : View
    {
        private const int Spokes = 12;
        private readonly View[] _spokes = new View[Spokes];
        private Animation _spin;

        public LoadingIndicator(string logoText = "Nuvio")
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = NuiFoundation.ThemeManager.Current.BgColor;

            var logo = new TextLabel
            {
                Text = logoText ?? "",
                PointSize = NuiFoundation.DesignTokens.TypeTitle,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Position(0, -160),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(logo);

            for (var i = 0; i < Spokes; i++)
            {
                var angle = i * (360f / Spokes);
                var spoke = new View
                {
                    Size = new Size(10, 42),
                    PositionUsesPivotPoint = true,
                    PivotPoint = new Vector3(0.5f, 1.0f, 0.5f),
                    ParentOrigin = new Vector3(0.5f, 0.5f, 0.5f),
                    BackgroundColor = new Color(1f, 1f, 1f, 0.25f + 0.75f * i / Spokes)
                };
                // Rotate each spoke outward from the center: offset via rotation
                // around the parent center using nested transform.
                spoke.Position = new Position(
                    (float)Math.Sin(angle * Math.PI / 180) * 70f - 5,
                    -(float)Math.Cos(angle * Math.PI / 180) * 70f + 21);
                _spokes[i] = spoke;
                Add(spoke);
            }
        }

        public void Start()
        {
            try
            {
                _spin?.Stop();
                _spin?.Dispose();
                _spin = new Animation(1000);
                foreach (var spoke in _spokes)
                {
                    _spin.AnimateBy(spoke, "Rotation", new Radian(new Degree(360f)));
                }
                _spin.Looping = true;
                _spin.Play();
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "spinner unavailable: " + ex.Message);
            }
        }

        public void Stop()
        {
            _spin?.Stop();
            _spin?.Dispose();
            _spin = null;
        }

        protected override void Dispose(DisposeTypes type)
        {
            Stop();
            base.Dispose(type);
        }
    }
}
