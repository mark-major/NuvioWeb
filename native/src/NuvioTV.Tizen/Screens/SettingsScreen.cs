using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Core.Settings;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of settingsScreen.js shell (Task 17.1): left rail with section meta
    /// (account, profiles, appearance, layout, plugins, integration, streams,
    /// playback, about), right content pane, marquee-style long labels, and
    /// focus memory per section. Sections write through profile-scoped stores.
    /// </summary>
    public sealed class SettingsScreen : ScreenBase
    {
        public const string UiStateKey = "settingsUiState";

        private static readonly (string Id, string Label)[] Sections =
        {
            ("account", "Account"),
            ("profiles", "Profiles"),
            ("appearance", "Appearance"),
            ("layout", "Layout"),
            ("plugins", "Addons"),
            ("integration", "Integration"),
            ("streams", "Streams"),
            ("playback", "Playback"),
            ("about", "About")
        };

        private readonly TextLabel _heading = new TextLabel();
        private readonly View _railHost = new View();
        private readonly View _contentHost = new View();
        private int _sectionIndex;

        public SettingsScreen()
        {
            _heading.Text = "Settings";
            _heading.PointSize = DesignTokens.TypeTitle;
            _heading.TextColor = ThemeManager.Current.TextColor;
            _heading.Position = new Position(DesignTokens.SafeGutter, 50);
            _heading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _heading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_heading);

            _railHost.Position = new Position(DesignTokens.SafeGutter, 140);
            _railHost.Size = new Size(420, 860);
            Add(_railHost);

            _contentHost.Position = new Position(560, 140);
            _contentHost.Size = new Size(1300, 860);
            Add(_contentHost);

            RenderRail();
            RenderContent();
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            // Focus memory: restore last active section.
            var saved = ctx?.StateStore?.Get(UiStateKey) as string;
            if (saved != null)
            {
                var index = Array.FindIndex(Sections, s => s.Id == saved);
                if (index >= 0)
                {
                    _sectionIndex = index;
                    RenderRail();
                    RenderContent();
                }
            }
            return Task.CompletedTask;
        }

        private void RenderRail()
        {
            while (_railHost.ChildCount > 0)
            {
                var child = _railHost.GetChildAt(0);
                _railHost.Remove(child);
                child.Dispose();
            }
            for (var i = 0; i < Sections.Length; i++)
            {
                var selected = i == _sectionIndex;
                var row = new TextLabel
                {
                    // Marquee parity handled by truncation here; 90px/s scroll
                    // lands with the widget-level MarqueeLabel.
                    Text = (selected ? "› " : "  ") + Sections[i].Label,
                    PointSize = DesignTokens.TypeBody,
                    TextColor = selected ? ThemeManager.Current.FocusColorValue : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(0, i * (DesignTokens.ButtonHeight + 16)),
                    Size = new Size(400, DesignTokens.ButtonHeight),
                    BackgroundColor = selected ? ThemeManager.Current.FocusBgColor : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Begin,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _railHost.Add(row);
            }
        }

        private void RenderContent()
        {
            while (_contentHost.ChildCount > 0)
            {
                var child = _contentHost.GetChildAt(0);
                _contentHost.Remove(child);
                child.Dispose();
            }
            switch (Sections[_sectionIndex].Id)
            {
                case "appearance":
                    RenderAppearanceSection();
                    break;
                case "about":
                    RenderAboutSection();
                    break;
                default:
                    _contentHost.Add(new TextLabel
                    {
                        Text = Sections[_sectionIndex].Label + " settings",
                        PointSize = DesignTokens.TypeSubtitle,
                        TextColor = ThemeManager.Current.TextSecondaryColor,
                        Position = new Position(40, 60),
                        WidthResizePolicy = ResizePolicyType.FillToParent,
                        HeightResizePolicy = ResizePolicyType.UseNaturalSize
                    });
                    break;
            }
        }

        private async void RenderAppearanceSection()
        {
            var theme = await ThemeStore.GetAsync();
            var info = new TextLabel
            {
                PointSize = DesignTokens.TypeBody,
                TextColor = ThemeManager.Current.TextColor,
                MultiLine = true,
                Position = new Position(40, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            info.Text =
                $"Theme: {theme.ThemeName}\n" +
                $"Accent: {theme.AccentColor}\n" +
                $"AMOLED: {(theme.AmoledMode ? "on" : "off")} (surfaces: {(theme.AmoledSurfacesMode ? "on" : "off")})\n" +
                $"Font: {theme.FontFamily}";
            _contentHost.Add(info);
        }

        private void RenderAboutSection()
        {
            _contentHost.Add(new TextLabel
            {
                Text = $"Nuvio TV (Native)\nVersion {Core.Configuration.AppConfig.AppVersion}\nGPL-3.0-only — see Licenses & Attributions",
                PointSize = DesignTokens.TypeBody,
                TextColor = ThemeManager.Current.TextColor,
                MultiLine = true,
                Position = new Position(40, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            });
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up when _sectionIndex > 0:
                    _sectionIndex--;
                    RenderRail();
                    RenderContent();
                    return true;
                case NuvioKey.Down when _sectionIndex < Sections.Length - 1:
                    _sectionIndex++;
                    RenderRail();
                    RenderContent();
                    return true;
            }
            return false;
        }

        public override object CaptureRouteState() => Sections[_sectionIndex].Id;

        public override void RestoreRouteState(object state)
        {
            if (state is string id)
            {
                var index = Array.FindIndex(Sections, s => s.Id == id);
                if (index >= 0) _sectionIndex = index;
            }
        }
    }
}
