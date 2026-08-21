using System;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Core.Settings;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Onboarding
{
    /// <summary>
    /// Port of experienceModeSelectionScreen.js: ESSENTIAL (modern, straight to
    /// home) vs ADVANCED (layout picker step). Back in layout step returns to
    /// mode step. Choices persist through ExperienceModeStore +
    /// LayoutPreferencesStore.
    /// </summary>
    public sealed class ExperienceModeSelectionScreen : ScreenBase
    {
        private const string ProfileId = "1";

        private enum Step { Mode, Layout }
        private Step _step = Step.Mode;
        private int _focusedIndex;

        private static readonly string[] Modes = { "ESSENTIAL", "ADVANCED" };
        private static readonly string[] Layouts = { "modern", "grid", "classic" };

        private readonly TextLabel _heading = new TextLabel();
        private readonly TextLabel[] _rows = new TextLabel[Math.Max(3, 2)];

        public ExperienceModeSelectionScreen()
        {
            _rows = new TextLabel[3];
            for (var i = 0; i < _rows.Length; i++)
            {
                var row = new TextLabel
                {
                    PointSize = DesignTokens.TypeSubtitle,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Position = new Position(0, 420 + i * (DesignTokens.ButtonHeight + 24))
                };
                _rows[i] = row;
                Add(row);
            }

            _heading.PointSize = DesignTokens.TypeTitle * 3 / 2;
            _heading.TextColor = ThemeManager.Current.TextColor;
            _heading.Position = new Position(0, 240);
            _heading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _heading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _heading.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_heading);
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _step = Step.Mode;
            _focusedIndex = 0;
            Render();
            return Task.CompletedTask;
        }

        private void Render()
        {
            _heading.Text = _step == Step.Mode ? "Choose your experience" : "Choose a home layout";
            for (var i = 0; i < _rows.Length; i++)
            {
                var visible = i < (_step == Step.Mode ? 2 : Layouts.Length);
                if (!visible)
                {
                    _rows[i].Text = "";
                    continue;
                }
                var label = _step == Step.Mode
                    ? Modes[i] == "ESSENTIAL" ? "Essential — just works" : "Advanced — pick a layout"
                    : Layouts[i];
                _rows[i].Text = (_focusedIndex == i ? "› " : "   ") + label;
                _rows[i].TextColor = _focusedIndex == i
                    ? ThemeManager.Current.FocusColorValue
                    : ThemeManager.Current.TextSecondaryColor;
            }
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            var count = _step == Step.Mode ? 2 : Layouts.Length;
            switch (key)
            {
                case NuvioKey.Up:
                    if (_focusedIndex > 0) { _focusedIndex--; Render(); }
                    return true;
                case NuvioKey.Down:
                    if (_focusedIndex < count - 1) { _focusedIndex++; Render(); }
                    return true;
                case NuvioKey.Ok:
                    Activate();
                    return true;
                case NuvioKey.Back when _step == Step.Layout:
                    _step = Step.Mode;
                    _focusedIndex = 0;
                    Render();
                    return true;
            }
            return false;
        }

        private void Activate()
        {
            if (_step == Step.Mode)
            {
                ChooseMode(Modes[_focusedIndex]);
            }
            else
            {
                ChooseLayout(Layouts[_focusedIndex]);
            }
        }

        private async void ChooseMode(string mode)
        {
            if (mode != "ADVANCED")
            {
                await LayoutPreferencesStore.SetAsync(ProfileId, new LayoutPreferencesData
                {
                    HasChosenLayout = true,
                    HomeLayout = "modern"
                });
                await ExperienceModeStore.SetAsync(ProfileId, new ExperienceModeSettings { Mode = "ESSENTIAL" });
                await AppServices.Router.NavigateAsync(Route.Home, new RouteParams(),
                    new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
            }
            _step = Step.Layout;
            _focusedIndex = 0;
            Render();
        }

        private async void ChooseLayout(string layout)
        {
            await LayoutPreferencesStore.SetAsync(ProfileId, new LayoutPreferencesData
            {
                HasChosenLayout = true,
                HomeLayout = layout
            });
            await ExperienceModeStore.SetAsync(ProfileId, new ExperienceModeSettings { Mode = "ADVANCED" });
            await AppServices.Router.NavigateAsync(Route.Home, new RouteParams(),
                new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
        }

        public override object ConsumeBackRequest() => null;
    }
}
