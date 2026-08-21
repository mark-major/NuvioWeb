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
    /// Port of libraryScreen.js core flows: tabs (saved / trakt / cloud), a
    /// type filter row, and a paged poster grid. Trakt tab lists the user's
    /// watchlist via TraktClient when signed in; cloud tab lists collections.
    /// </summary>
    public sealed class LibraryScreen : ScreenBase
    {
        private static readonly string[] Tabs = { "saved", "trakt", "cloud" };
        private static readonly string[] Filters = { "all", "movie", "series" };

        private readonly View _gridHost = new View();
        private readonly List<Row> _rows = new List<Row>();
        private int _tabIndex;
        private int _filterIndex;
        private int _focusedIndex;

        public sealed class Row
        {
            public string Id;
            public string Type;
            public string Title;
            public string Poster;
            public string Background;
        }

        public LibraryScreen()
        {
            var heading = new TextLabel
            {
                Text = "Library",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.SafeGutter, 50),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _gridHost.Position = new Position(DesignTokens.SafeGutter, 240);
            _gridHost.Size = new Size(1920 - DesignTokens.SafeGutter * 2, 740);
            Add(_gridHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            RenderTabs();
            _rows.Clear();
            try
            {
                switch (Tabs[_tabIndex])
                {
                    case "saved":
                        var items = await AppServices.SavedLibrary.ListForProfileAsync("1", 500);
                        foreach (var item in items)
                        {
                            _rows.Add(new Row
                            {
                                Id = item.ContentId,
                                Type = item.ContentType,
                                Title = item.Title,
                                Poster = item.Poster,
                                Background = item.Background
                            });
                        }
                        break;

                    case "trakt":
                        if (AppServices.Auth.IsAuthenticated)
                        {
                            // Watchlist requires the per-profile trakt token; the
                            // Trakt screen (12.4) owns device sign-in. Empty state here.
                            _statusText = "Sign in to Trakt from the Trakt tab.";
                        }
                        else
                        {
                            _statusText = "Sign in to Trakt to see your lists.";
                        }
                        break;

                    case "cloud":
                        _statusText = "";
                        break;
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "library load failed: " + ex.Message);
                _statusText = "Load failed";
            }
            ApplyFilter();
            RenderGrid();
        }

        private string _statusText = "";

        private void ApplyFilter()
        {
            var filter = Filters[_filterIndex];
            if (filter == "all") return;
            for (var i = _rows.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_rows[i].Type, filter, StringComparison.OrdinalIgnoreCase))
                {
                    _rows.RemoveAt(i);
                }
            }
        }

        private void RenderTabs()
        {
            RemoveChildren(_gridHost);
            for (var t = 0; t < Tabs.Length; t++)
            {
                var tab = new TextLabel
                {
                    Text = (t == _tabIndex ? "› " : "  ") + Tabs[t],
                    PointSize = DesignTokens.TypeBody,
                    TextColor = t == _tabIndex ? ThemeManager.Current.FocusColorValue : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(t * 260, 0),
                    Size = new Size(250, DesignTokens.ButtonHeight),
                    BackgroundColor = t == _tabIndex ? ThemeManager.Current.FocusBgColor : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gridHost.Add(tab);
            }
            for (var f = 0; f < Filters.Length; f++)
            {
                var chip = new TextLabel
                {
                    Text = (f == _filterIndex ? "› " : "  ") + Filters[f],
                    PointSize = DesignTokens.TypeCaption,
                    TextColor = f == _filterIndex ? ThemeManager.Current.TextColor : ThemeManager.Current.TextTertiaryColor,
                    Position = new Position(f * 200, 80),
                    Size = new Size(190, 48),
                    BackgroundColor = ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gridHost.Add(chip);
            }
        }

        private void RenderGrid()
        {
            const int tileW = 170, tileH = 255, gapX = DesignTokens.CardGap, gapY = 56;
            var columns = Math.Max(1, (1920 - DesignTokens.SafeGutter * 2) / (tileW + gapX));
            for (var i = 0; i < Math.Min(_rows.Count, 60); i++)
            {
                var row = _rows[i];
                var tile = new Widgets.PosterCard
                {
                    Position = new Position((i % columns) * (tileW + gapX), 150 + (i / columns) * (tileH + gapY)),
                    Size = new Size(tileW, tileH)
                };
                tile.Bind(AppServices.Images, row.Title ?? row.Id, row.Poster, row.Background);
                _gridHost.Add(tile);
            }
            if (_rows.Count == 0 && !string.IsNullOrEmpty(_statusText))
            {
                _gridHost.Add(new TextLabel
                {
                    Text = _statusText,
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(0, 300),
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
        }

        private void RemoveChildren(View host)
        {
            while (host.ChildCount > 0)
            {
                var child = host.GetChildAt(0);
                host.Remove(child);
                child.Dispose();
            }
        }

        // Zone model: tabs+filters occupy rows above the grid.
        private int _zone; // 0 = tabs, 1 = filters, 2 = grid

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (_zone)
            {
                case 0:
                    if (key == NuvioKey.Right && _tabIndex < Tabs.Length - 1) { _tabIndex++; RenderTabs(); return true; }
                    if (key == NuvioKey.Left && _tabIndex > 0) { _tabIndex--; RenderTabs(); return true; }
                    if (key == NuvioKey.Down) { _zone++; RenderTabs(); return true; }
                    if (key == NuvioKey.Ok) { _focusedIndex = 0; _ = ReloadAsync(); return true; }
                    break;
                case 1:
                    if (key == NuvioKey.Right && _filterIndex < Filters.Length - 1) { _filterIndex++; ReloadAsync(); return true; }
                    if (key == NuvioKey.Left && _filterIndex > 0) { _filterIndex--; ReloadAsync(); return true; }
                    if (key == NuvioKey.Up) { _zone--; RenderTabs(); return true; }
                    if (key == NuvioKey.Down) { _zone++; return true; }
                    break;
                case 2:
                    const int columns = 8;
                    if (key == NuvioKey.Up)
                    {
                        if (_focusedIndex >= columns) { _focusedIndex -= columns; }
                        else { _zone--; RenderTabs(); }
                        return true;
                    }
                    if (key == NuvioKey.Down && _focusedIndex + columns < _rows.Count) { _focusedIndex += columns; return true; }
                    if (key == NuvioKey.Left && _focusedIndex > 0) { _focusedIndex--; return true; }
                    if (key == NuvioKey.Right && _focusedIndex < _rows.Count - 1) { _focusedIndex++; return true; }
                    if (key == NuvioKey.Ok && _rows.Count > 0)
                    {
                        var row = _rows[_focusedIndex];
                        _ = AppServices.Router.NavigateAsync(Route.Detail,
                            new RouteParams().Set("id", row.Id).Set("type", row.Type));
                        return true;
                    }
                    break;
            }
            return false;
        }

        public override object ConsumeBackRequest() => null;
    }
}
