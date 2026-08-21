using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Integrations.Tmdb;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of castDetailScreen.js: TMDB person lookup by name or id, bio card,
    /// and a credits poster grid (combined_credits). Poster art falls back per
    /// the webapp chain; TMDB key comes from AppConfig.
    /// </summary>
    public sealed class CastDetailScreen : ScreenBase
    {
        private readonly TextLabel _name = new TextLabel();
        private readonly TextLabel _bio = new TextLabel();
        private readonly View _creditsHost = new View();
        private readonly List<TmdbClient.CreditEntry> _credits = new List<TmdbClient.CreditEntry>();
        private int _focusedIndex;

        public CastDetailScreen()
        {
            _name.PointSize = DesignTokens.TypeTitle;
            _name.TextColor = ThemeManager.Current.TextColor;
            _name.Position = new Position(DesignTokens.DetailSafeX, 100);
            _name.WidthResizePolicy = ResizePolicyType.FillToParent;
            _name.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_name);

            _bio.PointSize = DesignTokens.TypeBody;
            _bio.TextColor = ThemeManager.Current.TextSecondaryColor;
            _bio.MultiLine = true;
            _bio.Position = new Position(DesignTokens.DetailSafeX, 180);
            _bio.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 200);
            Add(_bio);

            _creditsHost.Position = new Position(DesignTokens.DetailSafeX, 420);
            _creditsHost.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 560);
            Add(_creditsHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var apiKey = AppConfig.TmdbApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                _name.Text = "TMDB not configured";
                return;
            }

            try
            {
                var client = new TmdbClient(AppServices.Http);
                var personId = p?.GetInt("castId") ?? 0;
                if (personId > 0)
                {
                    await LoadPersonAsync(client, apiKey, personId);
                    return;
                }

                var searchName = p?.GetString("castName");
                if (!string.IsNullOrEmpty(searchName))
                {
                    var results = await client.SearchPersonAsync(searchName, apiKey);
                    if (results.Count > 0)
                    {
                        await LoadPersonAsync(client, apiKey, results[0].Id);
                        return;
                    }
                }
                _name.Text = "Person not found";
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "cast detail failed: " + ex.Message);
                _name.Text = "Failed to load";
            }
        }

        private async Task LoadPersonAsync(TmdbClient client, string apiKey, int personId)
        {
            var person = await client.PersonAsync(personId, apiKey);
            _name.Text = person?.Name ?? "Unknown";
            _bio.Text = Truncate(person?.Biography ?? "", 600);

            _credits.Clear();
            if (person?.Credits?.Cast != null)
            {
                // Sort by date descending like the webapp credit rail.
                _credits.AddRange(person.Credits.Cast
                    .OrderByDescending(c => c.ReleaseDate ?? c.FirstAirDate ?? ""));
            }
            RenderCreditTiles();
        }

        private void RenderCreditTiles()
        {
            while (_creditsHost.ChildCount > 0)
            {
                var child = _creditsHost.GetChildAt(0);
                _creditsHost.Remove(child);
                child.Dispose();
            }

            const int tileW = 190, tileH = 285, gapX = DesignTokens.CardGap;
            for (var i = 0; i < Math.Min(_credits.Count, 40); i++)
            {
                var credit = _credits[i];
                var tile = new Widgets.NuvioImageView { Position = new Position((i % 8) * (tileW + gapX), (i / 8) * (tileH + 60)), Size = new Size(tileW, tileH) };
                tile.CornerRadiusValue = DesignTokens.CardRadius;
                tile.Bind(AppServices.Images);
                tile.Load(new[] { TmdbClient.ImageUrl(credit.PosterPath, "w342") });
                _creditsHost.Add(tile);
            }
        }

        public override bool OnKeyDown(NuvioKey key) => false;

        public override object ConsumeBackRequest() => null;

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text.Substring(0, max) + "…";
    }
}
