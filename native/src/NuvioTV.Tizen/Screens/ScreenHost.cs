using System;
using System.Threading.Tasks;
using NuvioTV.Tizen.Navigation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;
namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Single full-screen View container that mounts/unmounts one screen at a
    /// time with the route slide-in transition (160px translate, 240ms ease —
    /// Appendix C).
    /// </summary>
    public sealed class ScreenHost : View
    {
        private const int SlideDistancePx = 160;
        private const uint SlideDurationMs = 240;

        private ScreenBase _current;

        public ScreenHost()
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0.051f, 0.051f, 0.051f, 1f);
        }

        public ScreenBase CurrentScreen => _current;

        /// <summary>Removes the old screen and mounts the new one with a slide-in.</summary>
        public async Task MountAsync(ScreenBase screen, RouteParams p, NavigationContext ctx)
        {
            if (screen == null) throw new ArgumentNullException(nameof(screen));

            var previous = _current;
            if (previous != null)
            {
                Remove(previous);
                previous.Dispose();
            }

            screen.Size = new Size(SizeWidth, SizeHeight);
            Add(screen);

            // Enter from the right (slide-in), then settle at origin.
            screen.PositionX = SlideDistancePx;
            screen.Opacity = 0f;

            await screen.MountAsync(p ?? new RouteParams(), ctx);

            AnimateIn(screen);
            _current = screen;
        }

        private static void AnimateIn(View screen)
        {
            try
            {
                var animation = new Animation((int)SlideDurationMs);
                var easeOut = new AlphaFunction(AlphaFunction.BuiltinFunctions.EaseOut);
                animation.AnimateTo(screen, "PositionX", 0f, easeOut);
                animation.AnimateTo(screen, "Opacity", 1.0f, easeOut);
                animation.Play();
            }
            catch (Exception ex)
            {
                // Animation is cosmetic; land the screen even if it fails.
                screen.PositionX = 0f;
                screen.Opacity = 1f;
                global::Tizen.Log.Warn("NuvioTV", "route transition failed: " + ex.Message);
            }
        }
    }
}
