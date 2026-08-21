using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Shimmer skeleton tile (components.css loading keyframes): opacity pulse
    /// on a cardBg block. Hosts call Start()/Stop() with mount/unmount.
    /// </summary>
    public sealed class SkeletonLoader : View
    {
        private Animation _pulse;

        public SkeletonLoader()
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            CornerRadius = NuiFoundation.DesignTokens.CardRadius;
            BackgroundColor = NuiFoundation.ThemeManager.Current.CardBgColor;
        }

        public void Start()
        {
            if (_pulse != null) return;
            try
            {
                _pulse = new Animation(900);
                _pulse.AnimateTo(this, "Opacity", 0.45f, 0, 450);
                _pulse.AnimateTo(this, "Opacity", 1.0f, 450, 900);
                _pulse.Looping = true;
                _pulse.Play();
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "skeleton pulse unavailable: " + ex.Message);
            }
        }

        public void Stop()
        {
            if (_pulse == null) return;
            try
            {
                _pulse.Stop();
                _pulse.Dispose();
            }
            catch
            {
                // already gone with the animation loop
            }
            _pulse = null;
        }

        protected override void Dispose(DisposeTypes type)
        {
            Stop();
            base.Dispose(type);
        }
    }
}
