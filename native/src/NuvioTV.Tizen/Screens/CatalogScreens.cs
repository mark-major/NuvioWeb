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
    /// Shared behavior of search / discover / see-all surfaces (Task 12.1):
    /// assemble catalog rows across installed addons, render a poster grid with
    /// load-ahead paging (skip += 100 while hasMore), and open detail on OK.
    /// </summary>
    public abstract class CatalogGridScreenBase : ScreenBase
    {
        protected const int PageSize = 100;

        private readonly View _gridHost = new View();
        protected readonly List<Core.Models.Meta> Items = new List<Core.Models.Meta>();
        private readonly Core.Addons.CatalogRepository _catalogs =
            new Core.Addons.CatalogRepository(
                new Core.Addons.StremioAddonClient(
                    new global::NuvioTV.Core.Networking.NuvioHttpClient(
                        AppServices.Http, AppServices.Auth)));

        protected int _focusedIndex;
        private bool _loading;
        private string _statusText = "";

        protected abstract string HeadingText { get; }
        protected virtual string TypeFilter => "movie";
        protected virtual int Skip => Items.Count;

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var heading = new TextLabel
            {
                Text = HeadingText,
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 50),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _gridHost.Position = new Position(DesignTokens.SafeGutter, 150);
            _gridHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 830);
            Add(_gridHost);

            await LoadPageAsync();
        }

        /// <summary>Catalog requests for one page; implementations define targets.</summary>
        protected abstract Task<IReadOnlyList<Core.Models.CatalogRow>> FetchRowsAsync(int skip);

        protected async Task LoadPageAsync()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var rows = await FetchRowsAsync(Skip);
                foreach (var row in rows)
                {
                    foreach (var meta in row.Items)
                    {
                        if (Items.All(m => m.Id != meta.Id))
                        {
                            Items.Add(meta);
                        }
                    }
                }
                Render();
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "catalog page failed: " + ex.Message);
                _statusText = "Load failed";
            }
            finally
            {
                _loading = false;
            }
        }

        protected void RenderStatus(string text)
        {
            _statusText = text;
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

            const int tileW = 190, tileH = 285, gapX = DesignTokens.CardGap, gapY = 60;
            var columns = Math.Max(1, (1920 - DesignTokens.SafeGutter * 2) / (tileW + gapX));
            for (var i = 0; i < Items.Count && i < 300; i++)
            {
                var meta = Items[i];
                var tile = new Widgets.PosterCard
                {
                    Position = new Position((i % columns) * (tileW + gapX), (i / columns) * (tileH + gapY)),
                    Size = new Size(tileW, tileH)
                };
                tile.Bind(AppServices.Images, meta.Name ?? meta.Id,
                    meta.Poster, meta.Background, meta.Logo);
                _gridHost.Add(tile);
            }

            if (Items.Count == 0)
            {
                Add(new TextLabel
                {
                    Text = string.IsNullOrEmpty(_statusText) ? "Nothing here yet." : _statusText,
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(0, 300),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            var columns = Math.Max(1, (1920 - DesignTokens.SafeGutter * 2) / (190 + DesignTokens.CardGap));
            switch (key)
            {
                case NuvioKey.Left when _focusedIndex > 0:
                    _focusedIndex--;
                    return true;
                case NuvioKey.Right when _focusedIndex < Items.Count - 1:
                    _focusedIndex++;
                    return true;
                case NuvioKey.Up when _focusedIndex - columns >= 0:
                    _focusedIndex -= columns;
                    return true;
                case NuvioKey.Down when _focusedIndex + columns < Items.Count:
                    _focusedIndex += columns;
                    // Load-ahead: near the end fetch the next page.
                    if (Items.Count - _focusedIndex <= 12)
                    {
                        _ = LoadPageAsync();
                    }
                    return true;
                case NuvioKey.Ok when Items.Count > 0:
                    OpenDetail(Items[_focusedIndex]);
                    return true;
            }
            return false;
        }

        private void OpenDetail(Core.Models.Meta meta)
        {
            _ = AppServices.Router.NavigateAsync(Route.Detail, new RouteParams()
                .Set("id", meta.Id)
                .Set("type", meta.Type));
        }

        public override object ConsumeBackRequest() => null;
    }

    /// <summary>search= queries across all installed addons (searchCatalogTargets).</summary>
    public sealed class SearchScreen : CatalogGridScreenBase
    {
        private readonly Widgets.SearchField _field = new Widgets.SearchField("Search");
        private string _query = "";
        private IReadOnlyList<Core.Models.Addon> _addons = Array.Empty<Core.Models.Addon>();

        protected override string HeadingText => "Search";

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _addons = await AppServices.Addons.GetInstalledAddonsAsync();
            await base.MountAsync(p, ctx);

            _field.Position = new Position(DesignTokens.SafeGutter, 130);
            InsertAt(_field, 1);
        }

        private void InsertAt(View view, int index)
        {
            // TextField sits above the inherited heading; simple append keeps
            // z-order correct because heading was added first.
            Add(view);
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (_field.IsOnWindow)
            {
                if (_field.OnKey((global::NuvioTV.Core.Input.NuvioKey)(int)key, null))
                {
                    var changed = _query != _field.Text;
                    _query = _field.Text;
                    if (changed)
                    {
                        DebouncedSearchAsync();
                    }
                    return true;
                }
            }
            return base.OnKeyDown(key);
        }

        private bool _pending;
        private async void DebouncedSearchAsync()
        {
            _pending = true;
            await Task.Delay(400);
            if (!_pending) return;
            _pending = false;
            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            Items.Clear();
            _focusedIndex = 0;
            if (!string.IsNullOrWhiteSpace(_query))
            {
                await LoadPageAsync();
            }
        }

        protected override async Task<IReadOnlyList<Core.Models.CatalogRow>> FetchRowsAsync(int skip)
        {
            var result = new List<Core.Models.CatalogRow>();
            foreach (var addon in _addons.Take(10))
            {
                var catalog = addon.Catalogs?.FirstOrDefault(c =>
                    c.Type == "movie" || c.Type == "series");
                if (catalog == null) continue;
                try
                {
                    var row = await CatalogsForSearchAsync(addon, catalog, _query);
                    result.Add(row);
                }
                catch { /* per-addon failures are non-fatal */ }
            }
            return result;
        }

        private Task<Core.Models.CatalogRow> CatalogsForSearchAsync(
            Core.Models.Addon addon, Core.Models.AddonCatalog catalog, string query)
        {
            var repo = new Core.Addons.CatalogRepository(
                new Core.Addons.StremioAddonClient(
                    new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));
            var extras = new Dictionary<string, string> { ["search"] = query ?? "" };
            return repo.GetRowAsync(addon, catalog.Type, catalog.Id, 0, extras);
        }
    }

    /// <summary>Discover: catalogs without required extras across addons.</summary>
    public sealed class DiscoverScreen : CatalogGridScreenBase
    {
        protected override string HeadingText => "Discover";

        protected override async Task<IReadOnlyList<Core.Models.CatalogRow>> FetchRowsAsync(int skip)
        {
            var result = new List<Core.Models.CatalogRow>();
            var addons = await AppServices.Addons.GetInstalledAddonsAsync();
            var repo = new Core.Addons.CatalogRepository(
                new Core.Addons.StremioAddonClient(
                    new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));
            foreach (var addon in addons.Take(6))
            {
                foreach (var catalog in (addon.Catalogs ?? Array.Empty<Core.Models.AddonCatalog>()).Take(2))
                {
                    if (Core.Addons.HomeCatalogs.CatalogRequiresExtras(catalog)) continue;
                    try
                    {
                        result.Add(await repo.GetRowAsync(addon, catalog.Type, catalog.Id, skip));
                    }
                    catch { /* per-addon failures are non-fatal */ }
                }
            }
            return result;
        }
    }

    /// <summary>Generic paged view over a single catalog (see-all parity).</summary>
    public sealed class CatalogSeeAllScreen : CatalogGridScreenBase
    {
        private readonly string _type;
        private readonly string _catalogId;
        private readonly string _title;

        public CatalogSeeAllScreen(string type, string catalogId, string title = null)
        {
            _type = type ?? "movie";
            _catalogId = catalogId ?? "top";
            _title = title;
        }

        protected override string HeadingText => _title ?? $"{_type} · {_catalogId}";

        protected override async Task<IReadOnlyList<Core.Models.CatalogRow>> FetchRowsAsync(int skip)
        {
            var result = new List<Core.Models.CatalogRow>();
            var addons = await AppServices.Addons.GetInstalledAddonsAsync();
            var repo = new Core.Addons.CatalogRepository(
                new Core.Addons.StremioAddonClient(
                    new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));
            foreach (var addon in addons.Take(4))
            {
                try
                {
                    result.Add(await repo.GetRowAsync(addon, _type, _catalogId, skip));
                }
                catch { /* per-addon failures are non-fatal */ }
            }
            return result;
        }
    }
}
