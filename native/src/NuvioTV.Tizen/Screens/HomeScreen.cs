using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NuvioTV.Core.Home;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of homeScreen.js core flows: sidebar + catalog rows assembled over
    /// HomeCatalogs, hero rotation (20s first / 10s interval), L layout cycle,
    /// row paging budgets from HomeEngine, and OK → detail navigation.
    /// </summary>
    public sealed class HomeScreen : ScreenBase
    {
        private readonly View _rowsHost = new View();
        private readonly Widgets.SidebarNavigation _sidebar = new Widgets.SidebarNavigation();
        private readonly List<RowData> _rows = new List<RowData>();
        private int _focusedRow;
        private int _heroIndex;
        private System.Threading.Timer _heroTimer;
        private string _layout = "modern";

        private sealed class RowData
        {
            public string Title;
            public IReadOnlyList<Core.Models.Meta> Items = new List<Core.Models.Meta>();
            public int FocusedColumn;
            public int ScrollOffset;
        }

        public HomeScreen()
        {
            BackgroundColor = ThemeManager.Current.BgColor;

            _sidebar.Position = new Position(0, 0);
            _sidebar.Size = new Size(Widgets.SidebarNavigation.RailWidth, 1080);
            _sidebar.SetItems(new[]
            {
                new Widgets.SidebarNavigation.Item { Id = "home", Label = "Home" },
                new Widgets.SidebarNavigation.Item { Id = "search", Label = "Search" },
                new Widgets.SidebarNavigation.Item { Id = "library", Label = "Library" },
                new Widgets.SidebarNavigation.Item { Id = "settings", Label = "Settings" }
            });
            Add(_sidebar);

            _rowsHost.Position = new Position(Widgets.SidebarNavigation.RailWidth, 0);
            _rowsHost.Size = new Size(1920 - Widgets.SidebarNavigation.RailWidth, 1080);
            Add(_rowsHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            RenderRowsSkeleton();
            await LoadCatalogRowsAsync();
            StartHeroRotation();
        }

        private void RenderRowsSkeleton()
        {
            while (_rowsHost.ChildCount > 1)
            {
                var child = _rowsHost.GetChildAt(_rowsHost.ChildCount - 1);
                _rowsHost.Remove(child);
                child.Dispose();
            }
            for (var i = 0; i < 3; i++)
            {
                var skeleton = new Widgets.SkeletonLoader
                {
                    Position = new Position(DesignTokens.SafeGutter, 120 + i * 320),
                    Size = new Size(1500, 280)
                };
                skeleton.Start();
                _rowsHost.Add(skeleton);
            }
        }

        private async Task LoadCatalogRowsAsync()
        {
            try
            {
                var addons = await AppServices.Addons.GetInstalledAddonsAsync();
                var repo = new Core.Addons.CatalogRepository(
                    new Core.Addons.StremioAddonClient(
                        new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));

                // Initial pass: first catalog of the first addons without extras.
                var targets = addons
                    .SelectMany(a => (a.Catalogs ?? Array.Empty<Core.Models.AddonCatalog>())
                        .Where(c => !Core.Addons.HomeCatalogs.CatalogRequiresExtras(c))
                        .Take(1)
                        .Select(c => (Addon: a, Catalog: c)))
                    .Take(HomeEngine.InitialCatalogLoad());

                foreach (var target in targets)
                {
                    Core.Models.CatalogRow row;
                    try
                    {
                        var rowTask = repo.GetRowAsync(target.Addon, target.Catalog.Type,
                            target.Catalog.Id);
                        var completed = await Task.WhenAny(rowTask,
                            Task.Delay(HomeEngine.HomeRowTimeoutMs));
                        row = completed == rowTask ? rowTask.Result : null;
                    }
                    catch
                    {
                        continue; // per-row timeout is non-fatal
                    }

                    if (row == null || row.Items.Count == 0) continue;
                    lock (_rows)
                    {
                        _rows.Add(new RowData
                        {
                            Title = $"{target.Catalog.Type} · {target.Catalog.Name ?? target.Catalog.Id}",
                            Items = row.Items.Take(HomeEngine.MaxItemsPerRow()).ToList()
                        });
                    }
                    Render();
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "home rows failed: " + ex.Message);
            }
        }

        private void Render()
        {
            while (_rowsHost.ChildCount > 1)
            {
                var child = _rowsHost.GetChildAt(_rowsHost.ChildCount - 1);
                _rowsHost.Remove(child);
                child.Dispose();
            }

            List<RowData> snapshot;
            lock (_rows) { snapshot = _rows.ToList(); }

            const int tileW = 170, tileH = 255;
            for (var r = 0; r < snapshot.Count; r++)
            {
                var row = snapshot[r];
                var focusedRow = r == _focusedRow;

                var title = new TextLabel
                {
                    Text = (focusedRow ? "› " : "  ") + row.Title,
                    PointSize = DesignTokens.TypeSecondary,
                    TextColor = focusedRow ? ThemeManager.Current.TextColor : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(DesignTokens.SafeGutter - 20, 110 + r * 300),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize
                };
                _rowsHost.Add(title);

                for (var c = 0; c < row.Items.Count; c++)
                {
                    var meta = row.Items[c];
                    var tile = new Widgets.PosterCard(wide: false)
                    {
                        Position = new Position(
                            DesignTokens.SafeGutter - 20 + c * (tileW + DesignTokens.CardGap),
                            160 + r * 300),
                        Size = new Size(tileW, tileH)
                    };
                    tile.Bind(AppServices.Images, meta.Name ?? meta.Id, meta.Poster, meta.Background, meta.Logo);
                    tile.ApplyFocus(focusedRow && c == row.FocusedColumn);
                    _rowsHost.Add(tile);
                }
            }

            // Hero band placeholder art (backdrop of first row item).
            var heroItem = snapshot.FirstOrDefault()?.Items?.FirstOrDefault();
            if (heroItem != null)
            {
                var hero = new Widgets.NuvioImageView
                {
                    Position = new Position(DesignTokens.SafeGutter - 20, 10),
                    Size = new Size(1400, 90)
                };
                hero.Bind(AppServices.Images);
                hero.Load(new[] { heroItem.Background, heroItem.Poster });
                _rowsHost.Add(hero);
            }
        }

        /// <summary>Hero rotation timer (static art rotation; trailers deferred).</summary>
        private void StartHeroRotation()
        {
            StopHeroRotation();
            ScheduleNextHero(true);
        }

        private void ScheduleNextHero(bool first)
        {
            _heroTimer = new System.Threading.Timer(
                _ =>
                {
                    lock (_rows)
                    {
                        if (_rows.Count == 0) return;
                        _heroIndex = (_heroIndex + 1) % Math.Max(1, _rows[0].Items.Count);
                    }
                    NuiFoundation.AppServices.PostToUi?.Invoke(() => Render());
                    ScheduleNextHero(false);
                }, null,
                HomeEngine.NextHeroDelayMs(first), Timeout.Infinite);
        }

        private void StopHeroRotation()
        {
            _heroTimer?.Dispose();
            _heroTimer = null;
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            List<RowData> snapshot;
            lock (_rows) { snapshot = _rows.ToList(); }
            if (snapshot.Count == 0) return false;

            switch (key)
            {
                case NuvioKey.Up when _focusedRow > 0:
                    _focusedRow--;
                    Render();
                    return true;
                case NuvioKey.Down when _focusedRow < snapshot.Count - 1:
                    _focusedRow++;
                    Render();
                    return true;
                case NuvioKey.Left:
                    MoveColumn(snapshot, -1);
                    return true;
                case NuvioKey.Right:
                    MoveColumn(snapshot, +1);
                    return true;
                case NuvioKey.Ok:
                {
                    var row = snapshot[_focusedRow];
                    var meta = row.Items[row.FocusedColumn];
                    _ = AppServices.Router.NavigateAsync(Route.Detail,
                        new RouteParams().Set("id", meta.Id).Set("type", meta.Type));
                    return true;
                }
                case NuvioKey.LetterL:
                    _layout = HomeEngine.NextLayout(_layout);
                    global::Tizen.Log.Info("NuvioTV", "home layout -> " + _layout);
                    return true;
            }
            return false;
        }

        private void MoveColumn(List<RowData> rows, int delta)
        {
            var row = rows[_focusedRow];
            var next = row.FocusedColumn + delta;
            if (next < 0 || next >= row.Items.Count) return;
            row.FocusedColumn = next;
            Render();
        }

        public override void Cleanup() => StopHeroRotation();

        public override object ConsumeBackRequest() => null;

        private static class Timeout
        {
            public const int Infinite = System.Threading.Timeout.Infinite;
        }
    }
}
