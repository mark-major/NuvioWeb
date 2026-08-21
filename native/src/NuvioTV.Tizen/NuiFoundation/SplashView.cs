using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Logo splash shown behind the BootGuard overlay and kept mounted until the
    /// first screen mounts (Phase 9+). Mirrors the webapp shell background.
    /// </summary>
    public sealed class SplashView : View
    {
        public SplashView()
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;
            BackgroundColor = new Color(0.051f, 0.051f, 0.051f, 1f); // #0d0d0d

            var logo = new TextLabel
            {
                Text = "Nuvio",
                PointSize = 96f,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Position(0, -40),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(logo);

            var tagline = new TextLabel
            {
                Text = "Nuvio TV (Native)",
                PointSize = DesignTokens.TypeBody,
                TextColor = new Color(0.702f, 0.702f, 0.702f, 1f), // #b3b3b3
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Position = new Position(0, 80),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(tagline);
        }
    }
}
