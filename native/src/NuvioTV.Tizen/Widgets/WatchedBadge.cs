using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>"Watched" corner badge (components.css .watched-badge parity).</summary>
    public sealed class WatchedBadge : View
    {
        private readonly TextLabel _label = new TextLabel
        {
            Text = "✓",
            PointSize = NuiFoundation.DesignTokens.TypeCaption,
            TextColor = Color.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            WidthResizePolicy = ResizePolicyType.FillToParent,
            HeightResizePolicy = ResizePolicyType.FillToParent
        };

        public WatchedBadge()
        {
            PositionUsesPivotPoint = true;
            PivotPoint = new Vector3(1f, 0f, 0.5f);
            ParentOrigin = Position.ParentOriginTopRight;
            BackgroundColor = new Color(0f, 0f, 0f, 0.75f);
            Position = new Position(-12, 12);
            Add(_label);
        }
    }
}
