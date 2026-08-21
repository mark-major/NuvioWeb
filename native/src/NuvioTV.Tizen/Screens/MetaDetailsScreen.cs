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
    /// Port of metaDetailsScreen.js core flows: hero (backdrop rotation only —
    /// trailer surfaces removed per v1 decision), action buttons, season row +
    /// episode rail with hold-repeat acceleration seam, and stream chooser
    /// entry. Back-state focus restore via Router state store.
    /// </summary>
    public sealed class MetaDetailsScreen : ScreenBase
    {
        private readonly Core.Addons.MetaRepository _metaRepo =
            new Core.Addons.MetaRepository(
                new Core.Addons.StremioAddonClient(
                    new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));

        private readonly Widgets.NuvioImageView _backdrop = new Widgets.NuvioImageView();
        private readonly TextLabel _title = new TextLabel();
        private readonly TextLabel _description = new TextLabel();
        private readonly View _seasonRow = new View();
        private readonly View _episodeRow = new View();
        private readonly Detail.DetailEnrichmentView _enrichment = new Detail.DetailEnrichmentView();

        private Core.Models.Meta _meta;
        private List<Core.Models.MetaVideo> _seasonEpisodes = new List<Core.Models.MetaVideo>();
        private int _zone; // 0 hero, 1 seasons, 2 episodes
        private int _seasonIndex = 1;
        private int _episodeIndex;

        public MetaDetailsScreen()
        {
            _backdrop.Position = new Position(0, 0);
            _backdrop.Size = new Size(1920, 560);
            Add(_backdrop);

            _title.PointSize = DesignTokens.TypeTitle * 3 / 2;
            _title.TextColor = ThemeManager.Current.TextColor;
            _title.Position = new Position(DesignTokens.DetailSafeX, 420);
            _title.WidthResizePolicy = ResizePolicyType.FillToParent;
            _title.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_title);

            _description.PointSize = DesignTokens.TypeBody;
            _description.TextColor = ThemeManager.Current.TextSecondaryColor;
            _description.MultiLine = true;
            _description.Position = new Position(DesignTokens.DetailSafeX, 560);
            _description.Size = new Size(1400, 160);
            Add(_description);

            _seasonRow.Position = new Position(DesignTokens.DetailSafeX, 740);
            _seasonRow.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 80);
            Add(_seasonRow);

            _episodeRow.Position = new Position(DesignTokens.DetailSafeX, 850);
            _episodeRow.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 180);
            Add(_episodeRow);

            _enrichment.Position = new Position(DesignTokens.DetailSafeX, 1050);
            Add(_enrichment);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var id = p?.GetString("id") ?? "";
            var type = p?.GetString("type") ?? "movie";

            try
            {
                var addons = await AppServices.Addons.GetInstalledAddonsAsync();
                _meta = await _metaRepo.GetFromAllAddonsAsync(type, id, addons);
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "detail load failed: " + ex.Message);
            }

            RenderMeta();
            if (_meta != null)
            {
                await _enrichment.LoadAsync(_meta.Name, _meta.Id);
            }
            await Task.CompletedTask;
        }

        private void RenderMeta()
        {
            if (_meta == null)
            {
                _title.Text = "Not found";
                return;
            }
            _title.Text = _meta.Name ?? _meta.Id;
            _description.Text = Truncate(_meta.Description ?? "", 400);

            _backdrop.Bind(AppServices.Images);
            _backdrop.Load(new[] { _meta.Background, _meta.Poster, _meta.Logo });

            if (_meta.Videos != null && _meta.Videos.Count > 0)
            {
                RenderSeasons();
            }
        }

        private void RenderSeasons()
        {
            while (_seasonRow.ChildCount > 0)
            {
                var child = _seasonRow.GetChildAt(0);
                _seasonRow.Remove(child);
                child.Dispose();
            }
            var seasons = SeasonNumbers();
            for (var i = 0; i < Math.Min(seasons.Count, 12); i++)
            {
                var chip = new TextLabel
                {
                    Text = $"S{seasons[i]}",
                    PointSize = DesignTokens.TypeSecondary,
                    TextColor = seasons[i] == _seasonIndex
                        ? ThemeManager.Current.FocusColorValue
                        : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(i * 130, 10),
                    Size = new Size(120, 60),
                    BackgroundColor = ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _seasonRow.Add(chip);
            }
            RenderEpisodes();
        }

        private void RenderEpisodes()
        {
            while (_episodeRow.ChildCount > 0)
            {
                var child = _episodeRow.GetChildAt(0);
                _episodeRow.Remove(child);
                child.Dispose();
            }
            _seasonEpisodes = (_meta.Videos ?? Array.Empty<Core.Models.MetaVideo>())
                .Where(v => (v.Season ?? v.SeasonNumber ?? 0) == _seasonIndex)
                .ToList();

            const int tileW = 260, gap = DesignTokens.CardGap;
            for (var i = 0; i < Math.Min(_seasonEpisodes.Count, 20); i++)
            {
                var ep = _seasonEpisodes[i];
                var focused = _zone == 2 && i == _episodeIndex;
                var tile = new TextLabel
                {
                    Text = $"E{(ep.Episode ?? ep.EpisodeNumber ?? i + 1)}\n{Truncate(ep.Title ?? "", 40)}",
                    PointSize = DesignTokens.TypeCaption,
                    MultiLine = true,
                    TextColor = focused ? ThemeManager.Current.TextColor : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(i * (tileW + gap), 10),
                    Size = new Size(tileW, 150),
                    BackgroundColor = focused ? ThemeManager.Current.FocusBgColor : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _episodeRow.Add(tile);
            }
        }

        private List<int> SeasonNumbers() =>
            (_meta.Videos ?? Array.Empty<Core.Models.MetaVideo>())
                .Select(v => v.Season ?? v.SeasonNumber ?? 0)
                .Where(s => s > 0)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Down when _zone < 2:
                    _zone++;
                    RenderSeasons();
                    return true;
                case NuvioKey.Up when _zone > 0:
                    _zone--;
                    RenderSeasons();
                    return true;
                case NuvioKey.Left when _zone >= 1 && _episodeIndex > 0:
                    _episodeIndex--;
                    RenderEpisodes();
                    return true;
                case NuvioKey.Right when _zone >= 1 && _episodeIndex < _seasonEpisodes.Count - 1:
                    _episodeIndex++;
                    RenderEpisodes();
                    return true;
                case NuvioKey.Ok when _zone == 2 && _seasonEpisodes.Count > 0:
                {
                    var ep = _seasonEpisodes[Math.Min(_episodeIndex, _seasonEpisodes.Count - 1)];
                    _ = AppServices.Router.NavigateAsync(Route.Stream,
                        new RouteParams().Set("id", ep.Id).Set("type", "series"));
                    return true;
                }
                case NuvioKey.Ok when _zone <= 1 && _meta != null:
                {
                    _ = AppServices.Router.NavigateAsync(Route.Stream,
                        new RouteParams().Set("id", _meta.Id).Set("type", _meta.Type));
                    return true;
                }
            }
            return false;
        }

        public override object CaptureRouteState() =>
            new[] { _zone, _seasonIndex, _episodeIndex };

        public override void RestoreRouteState(object state)
        {
            if (state is int[] saved && saved.Length == 3)
            {
                _zone = saved[0];
                _seasonIndex = saved[1];
                _episodeIndex = saved[2];
            }
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text.Substring(0, max) + "…";
    }
}
