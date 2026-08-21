using System;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Onboarding
{
    /// <summary>
    /// Port of essentialAddonSetupScreen.js: explains that at least one addon
    /// is needed and offers skip (OK) — the addon install flow itself lives in
    /// PluginScreen (Task 11.3).
    /// </summary>
    public sealed class EssentialAddonSetupScreen : ScreenBase
    {
        private readonly TextLabel _heading = new TextLabel();
        private readonly TextLabel _body = new TextLabel();
        private readonly TextLabel _action = new TextLabel();

        public EssentialAddonSetupScreen()
        {
            _heading.Text = "Add a catalog source";
            _heading.PointSize = DesignTokens.TypeTitle * 3 / 2;
            _heading.Position = new Position(0, 260);
            _heading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _heading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _heading.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_heading);

            _body.Text = "Nuvio needs at least one addon to load catalogs,\nstreams and subtitles.";
            _body.PointSize = DesignTokens.TypeSubtitle;
            _body.TextColor = ThemeManager.Current.TextSecondaryColor;
            _body.MultiLine = true;
            _body.Position = new Position(0, 420);
            _body.WidthResizePolicy = ResizePolicyType.FillToParent;
            _body.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _body.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_body);

            _action.Text = "› Skip for now (OK)";
            _action.PointSize = DesignTokens.TypeBody;
            _action.TextColor = ThemeManager.Current.FocusColorValue;
            _action.Position = new Position(0, 620);
            _action.WidthResizePolicy = ResizePolicyType.FillToParent;
            _action.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _action.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_action);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            // ExperienceModeStore.addonSetupSkipped drives whether this shows again.
            await Task.CompletedTask;
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (key != NuvioKey.Ok) return false;
            Skip();
            return true;
        }

        private async void Skip()
        {
            await Core.Settings.ExperienceModeStore.SetAsync("1",
                new Core.Settings.ExperienceModeSettings { Mode = "ESSENTIAL", AddonSetupSkipped = true });
            await AppServices.Router.NavigateAsync(Route.Home, new RouteParams(),
                new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
        }

        public override object ConsumeBackRequest() => null;
    }
}
