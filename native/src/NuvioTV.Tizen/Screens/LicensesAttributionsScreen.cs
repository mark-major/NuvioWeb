using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of licensesAttributionsScreen.js: static license list extended with
    /// the native NuGet dependencies (plan constraint: GPL-compatible licenses
    /// recorded here).
    /// </summary>
    public sealed class LicensesAttributionsScreen : ScreenBase
    {
        private sealed class LicenseRow
        {
            public string Name;
            public string License;
            public string Url;
        }

        private int _focusedIndex;

        private static readonly List<LicenseRow> Rows = new List<LicenseRow>
        {
            new LicenseRow { Name = "Nuvio Web (this project)", License = "GPL-3.0-only", Url = "" },
            new LicenseRow { Name = "Tizen.NET.API7", License = "Apache-2.0", Url = "https://github.com/Samsung/Tizen.NET" },
            new LicenseRow { Name = "Tizen.NET.Sdk", License = "MIT", Url = "https://github.com/Samsung/Tizen.NET" },
            new LicenseRow { Name = "System.Text.Json", License = "MIT", Url = "https://github.com/dotnet/runtime" },
            new LicenseRow { Name = "Net.Codecrete.QrCodeGenerator", License = "MIT", Url = "https://github.com/manuelbl/QrCodeGenerator" },
            new LicenseRow { Name = "xUnit", License = "Apache-2.0", Url = "https://xunit.net" },
            new LicenseRow { Name = "Inter font", License = "SIL OFL 1.1", Url = "https://rsms.me/inter/" },
            new LicenseRow { Name = "DM Sans font", License = "SIL OFL 1.1", Url = "https://fonts.google.com/specimen/DM+Sans" },
            new LicenseRow { Name = "Open Sans font", License = "SIL OFL 1.1", Url = "https://fonts.google.com/specimen/Open+Sans" },
            new LicenseRow { Name = "Material Icons font", License = "Apache-2.0", Url = "https://material.io/icons" }
        };

        public LicensesAttributionsScreen()
        {
            var heading = new TextLabel
            {
                Text = "Licenses & Attributions",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.DetailSafeX, 100),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            for (var i = 0; i < Rows.Count; i++)
            {
                var row = new TextLabel
                {
                    Text = $"{Rows[i].Name} — {Rows[i].License}",
                    PointSize = DesignTokens.TypeBody,
                    Position = new Position(DesignTokens.DetailSafeX, 200 + i * 64),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize
                };
                Add(row);
            }
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            Highlight(0);
            return Task.CompletedTask;
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up when _focusedIndex > 0:
                    Highlight(--_focusedIndex);
                    return true;
                case NuvioKey.Down when _focusedIndex < Rows.Count - 1:
                    Highlight(++_focusedIndex);
                    return true;
            }
            return false;
        }

        private void Highlight(int index) { /* row highlight is informational */ }

        public override object ConsumeBackRequest() => null;
    }
}
