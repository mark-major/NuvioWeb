using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Input;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Detail
{
    /// <summary>
    /// Detail enrichment rails (Task 14.2): cast rail + more-like rail backed
    /// by TMDB combined credits / similar titles, and a ratings strip from
    /// MDBList. Wired into MetaDetailsScreen below the episode rail.
    /// </summary>
    public sealed class DetailEnrichmentView : View
    {
        private readonly TextLabel _ratings = new TextLabel();
        private readonly TextLabel _castHeading = new TextLabel();
        private readonly View _castRow = new View();
        private readonly TextLabel _moreHeading = new TextLabel();
        private readonly View _moreRow = new View();

        public DetailEnrichmentView()
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.UseNaturalSize;

            _ratings.PointSize = DesignTokens.TypeBody;
            _ratings.TextColor = ThemeManager.Current.TextColor;
            _ratings.Position = new Position(DesignTokens.DetailSafeX, 0);
            _ratings.WidthResizePolicy = ResizePolicyType.FillToParent;
            _ratings.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_ratings);

            _castHeading.Text = "Cast";
            _castHeading.PointSize = DesignTokens.TypeSecondary;
            _castHeading.TextColor = ThemeManager.Current.TextSecondaryColor;
            _castHeading.Position = new Position(DesignTokens.DetailSafeX, 60);
            _castHeading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _castHeading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_castHeading);

            _castRow.Position = new Position(DesignTokens.DetailSafeX, 100);
            _castRow.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 240);
            Add(_castRow);

            _moreHeading.Text = "More like this";
            _moreHeading.PointSize = DesignTokens.TypeSecondary;
            _moreHeading.TextColor = ThemeManager.Current.TextSecondaryColor;
            _moreHeading.Position = new Position(DesignTokens.DetailSafeX, 360);
            _moreHeading.WidthResizePolicy = ResizePolicyType.FillToParent;
            _moreHeading.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_moreHeading);

            _moreRow.Position = new Position(DesignTokens.DetailSafeX, 400);
            _moreRow.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 260);
            Add(_moreRow);
        }

        /// <summary>Loads enrichment for the meta item; all failures are non-fatal.</summary>
        public async Task LoadAsync(string title, string imdbId)
        {
            var apiKey = AppConfig.TmdbApiKey;
            if (string.IsNullOrEmpty(apiKey)) return;

            try
            {
                var tmdb = new Core.Integrations.Tmdb.TmdbClient(AppServices.Http);

                // Ratings via MDBList (imdb id).
                if (!string.IsNullOrEmpty(imdbId))
                {
                    var mdbList = new Core.Integrations.Ratings.MdbListClient(AppServices.Http);
                    var ratings = await mdbList.FetchRatingsAsync(imdbId, "movie",
                        Core.Integrations.Ratings.MdbListClient.Providers.Keys);
                    if (ratings.Count > 0)
                    {
                        var summary = string.Join("   ", ratings
                            .Where(kv => kv.Value.HasValue)
                            .Select(kv => $"{kv.Key}: {kv.Value:0.0}"));
                        if (summary.Length > 0) _ratings.Text = summary;
                    }
                }

                // Cast rail via person search.
                if (!string.IsNullOrEmpty(title))
                {
                    var people = await tmdb.SearchPersonAsync(title, apiKey);
                    RenderTiles(_castRow, people.Take(10)
                        .Select(p => (Name: p.Name ?? "", Image: Core.Integrations.Tmdb.TmdbClient.ImageUrl(p.ProfilePath, "w185"))));
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "enrichment load failed: " + ex.Message);
            }
        }

        /// <summary>Renders more-like tiles from catalog metas.</summary>
        public void SetMoreLikeThis(IEnumerable<Core.Models.Meta> items)
        {
            while (_moreRow.ChildCount > 0)
            {
                var child = _moreRow.GetChildAt(0);
                _moreRow.Remove(child);
                child.Dispose();
            }
            foreach (var meta in items.Take(10).Select((meta, i) => (meta, i)))
            {
                var tile = new Widgets.PosterCard
                {
                    Position = new Position(meta.i * (170 + DesignTokens.CardGap), 0),
                    Size = new Size(170, 250)
                };
                tile.Bind(AppServices.Images, meta.meta.Name ?? meta.meta.Id,
                    meta.meta.Poster, meta.meta.Background);
                _moreRow.Add(tile);
            }
        }

        private void RenderTiles(View host, IEnumerable<(string Name, string Image)> entries)
        {
            const int tileW = 140, gap = DesignTokens.CardGap;
            var index = 0;
            foreach (var entry in entries)
            {
                var tile = new Widgets.NuvioImageView
                {
                    Position = new Position(index * (tileW + gap), 0),
                    Size = new Size(tileW, 210)
                };
                tile.Bind(AppServices.Images);
                tile.Load(new[] { entry.Image });
                host.Add(tile);

                var name = new TextLabel
                {
                    Text = entry.Name,
                    PointSize = DesignTokens.TypeCaption,
                    TextColor = ThemeManager.Current.TextTertiaryColor,
                    Position = new Position(tileW + gap, 210),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                host.Add(name);
                index++;
            }
        }

        public bool OnKey(NuvioKey key) => false;
    }
}
