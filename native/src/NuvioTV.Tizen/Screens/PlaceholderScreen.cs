using System;
using System.Threading.Tasks;
using NuvioTV.Tizen.Navigation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Temporary route target while screens are ported phase by phase; shows the
    /// route name so on-device smoke tests can verify navigation semantics.
    /// </summary>
    public sealed class PlaceholderScreen : ScreenBase
    {
        private readonly string _title;

        public PlaceholderScreen(string title)
        {
            _title = title;

            var label = new TextLabel
            {
                Text = _title,
                PointSize = NuiFoundation.DesignTokens.TypeTitle,
                TextColor = Color.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.FillToParent
            };
            Add(label);
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            global::Tizen.Log.Info("NuvioTV", $"placeholder mounted: {_title} (back={ctx.IsBackNavigation})");
            return Task.CompletedTask;
        }
    }
}
