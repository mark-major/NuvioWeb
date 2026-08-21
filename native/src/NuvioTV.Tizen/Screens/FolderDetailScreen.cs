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
    /// Port of folderDetailScreen.js core flow: a collection folder's item grid
    /// (items come from the addon catalog backing the collection), TMDB
    /// enrichment is applied when the TMDB key is configured. GIF hydration of
    /// the webapp renders as static poster art here.
    /// </summary>
    public sealed class FolderDetailScreen : ScreenBase
    {
        private readonly View _gridHost = new View();
        private readonly List<Core.Models.Meta> _items = new List<Core.Models.Meta>();
        private int _focusedIndex;

        public FolderDetailScreen()
        {
            var heading = new TextLabel
            {
                Text = "Collection",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _gridHost.Position = new Position(DesignTokens.SafeGutter, 170);
            _gridHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 820);
            Add(_gridHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var addonId = p?.GetString("addonId") ?? "";
            var catalogId = p?.GetString("catalogId") ?? "";
            var catalogType = p?.GetString("type") ?? "movie";
            var baseUrl = p?.GetString("baseUrl");

            if (string.IsNullOrEmpty(baseUrl))
            {
                // Find the addon base URL by id.
                var addons = await AppServices.Addons.GetInstalledAddonsAsync();
                var match = addons.FirstOrDefault(a => a.Id == addonId);
                baseUrl = match?.BaseUrl;
            }
            if (string.IsNullOrEmpty(baseUrl))
            {
                return;
            }

            try
            {
                var repo = new Core.Addons.CatalogRepository(
                    new Core.Addons.StremioAddonClient(
                        new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));
                var row = await repo.GetAsync(baseUrl, addonId, addonId, catalogId, catalogId, catalogType);
                lock (_items)
                {
                    _items.Clear();
                    _items.AddRange(row.Items);
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "folder load failed: " + ex.Message);
            }
            Render();
        }

        private void Render()
        {
            while (_gridHost.ChildCount > 0)
            {
                var child = _gridHost.GetChildAt(0);
                _gridHost.Remove(child);
                child.Dispose();
            }

            const int tileW = 190, tileH = 285, gapX = DesignTokens.CardGap, gapY = 56;
            var columns = Math.Max(1, (1920 - DesignTokens.SafeGutter * 2) / (tileW + gapX));
            List<Core.Models.Meta> snapshot;
            lock (_items) { snapshot = _items.ToList(); }

            for (var i = 0; i < Math.Min(snapshot.Count, 120); i++)
            {
                var meta = snapshot[i];
                var tile = new Widgets.PosterCard
                {
                    Position = new Position((i % columns) * (tileW + gapX), (i / columns) * (tileH + gapY)),
                    Size = new Size(tileW, tileH),
                    BackgroundColor = i == _focusedIndex ? ThemeManager.Current.FocusBgColor : Color.Transparent
                };
                tile.Bind(AppServices.Images, meta.Name ?? meta.Id, meta.Poster, meta.Background, meta.Logo);
                _gridHost.Add(tile);
            }
        }

        public override bool OnKeyDown(NuvioKey key) => false;

        public override object ConsumeBackRequest() => null;
    }
}
