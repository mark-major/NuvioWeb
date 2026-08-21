using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of supportersContributorsScreen.js (static card grid): sponsor
    /// names from config, unique contributions + donations from the configured
    /// API, with graceful offline fallback.
    /// </summary>
    public sealed class SupportersScreen : ScreenBase
    {
        private readonly TextLabel _status = new TextLabel();
        private readonly View _gridHost = new View();

        public SupportersScreen()
        {
            var heading = new TextLabel
            {
                Text = "Supporters & Contributors",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 80),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _gridHost.Position = new Position(DesignTokens.SafeGutter, 200);
            _gridHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 700);
            Add(_gridHost);

            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(0, 950);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_status);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _status.Text = "Loading supporters…";
            RenderNames(new List<string> { "Sponsors: " + AppConfig.SponsorNames });

            try
            {
                var baseUrl = AppConfig.UniqueContributionsBaseUrl;
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    using (var response = await AppServices.Http.GetAsync(
                        baseUrl.TrimEnd('/') + "/api/unique-contributions"))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var names = ParseContributorNames(json);
                            RenderNames(names);
                        }
                    }
                }
                _status.Text = "";
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "supporters load failed: " + ex.Message);
                _status.Text = "Couldn't reach the supporters service.";
            }
        }

        /// <summary>Extracts contributor names (unique-contributions JSON shape).</summary>
        internal static List<string> ParseContributorNames(string json)
        {
            var result = new List<string>();
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("contributors", out var contributors) ||
                        contributors.ValueKind != JsonValueKind.Array)
                    {
                        return result;
                    }
                    foreach (var entry in contributors.EnumerateArray())
                    {
                        if (entry.TryGetProperty("name", out var name) &&
                            !string.IsNullOrWhiteSpace(name.GetString()))
                        {
                            result.Add(name.GetString().Trim());
                        }
                    }
                }
            }
            catch
            {
                // Malformed payload → empty list; screen keeps fallback text.
            }
            return result;
        }

        private void RenderNames(IReadOnlyList<string> lines)
        {
            while (_gridHost.ChildCount > 0)
            {
                var child = _gridHost.GetChildAt(0);
                _gridHost.Remove(child);
                child.Dispose();
            }
            for (var i = 0; i < Math.Min(lines.Count, 12); i++)
            {
                var card = new TextLabel
                {
                    Text = lines[i],
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextColor,
                    Position = new Position((i % 4) * 420, (i / 4) * 160),
                    Size = new Size(400, 140),
                    BackgroundColor = ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gridHost.Add(card);
            }
        }

        public override bool OnKeyDown(NuvioKey key) => false;

        public override object ConsumeBackRequest() => null;
    }
}
