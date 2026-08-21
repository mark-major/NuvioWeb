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
    /// Port of streamScreen.js core flows: stream rows across addons with
    /// badges from Core.Streams, P2P-unavailable rows (InfoHash set, no URL,
    /// no debrid path) render dimmed with a native_p2p_unavailable toast on
    /// OK, playable rows navigate to the Player (Phase 16 wires playback).
    /// </summary>
    public sealed class StreamScreen : ScreenBase
    {
        private sealed class Row
        {
            public Core.Models.StreamItem Stream;
            public string AddonName;
            public bool P2pUnavailable;
        }

        private readonly View _listHost = new View();
        private readonly List<Row> _rows = new List<Row>();
        private int _focusedIndex;

        public StreamScreen()
        {
            var heading = new TextLabel
            {
                Text = "Streams",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.DetailSafeX, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _listHost.Position = new Position(DesignTokens.DetailSafeX, 160);
            _listHost.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 840);
            Add(_listHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var type = p?.GetString("type") ?? "movie";
            var id = p?.GetString("id") ?? "";
            try
            {
                var repo = new Core.Addons.StreamRepository(
                    new Core.Addons.StremioAddonClient(
                        new global::NuvioTV.Core.Networking.NuvioHttpClient(AppServices.Http, AppServices.Auth)));
                var addons = await AppServices.Addons.GetInstalledAddonsAsync();
                var groups = await repo.GetStreamsFromAllAddonsAsync(addons, type, id);

                foreach (var group in groups)
                {
                    foreach (var stream in group.Streams ?? Array.Empty<Core.Models.StreamItem>())
                    {
                        // Plan Task 12.3 P2P rule: torrent without URL and no debrid path.
                        var p2pUnavailable =
                            !string.IsNullOrEmpty(stream.InfoHash) &&
                            string.IsNullOrEmpty(stream.Url) &&
                            string.IsNullOrEmpty(stream.ExternalUrl);
                        _rows.Add(new Row
                        {
                            Stream = stream,
                            AddonName = group.AddonName,
                            P2pUnavailable = p2pUnavailable
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "stream fetch failed: " + ex.Message);
            }
            Render();
        }

        private void Render()
        {
            while (_listHost.ChildCount > 0)
            {
                var child = _listHost.GetChildAt(0);
                _listHost.Remove(child);
                child.Dispose();
            }

            for (var i = 0; i < Math.Min(_rows.Count, 14); i++)
            {
                var row = _rows[i];
                var focused = i == _focusedIndex;
                var title = Core.Streams.StreamDisplayText.NormalizeMathematicalAlphanumericSymbols(
                    row.Stream.Title ?? row.Stream.Name ?? "Stream");
                var quality = row.Stream.Quality ?? "";

                var card = new TextLabel
                {
                    Text = $"{(focused ? "› " : "  ")}{quality}  {title}" +
                           (string.IsNullOrEmpty(row.AddonName) ? "" : $"   · {row.AddonName}"),
                    PointSize = DesignTokens.TypeBody,
                    MultiLine = true,
                    TextColor = row.P2pUnavailable
                        ? ThemeManager.Current.TextTertiaryColor
                        : focused ? ThemeManager.Current.TextColor : ThemeManager.Current.TextSecondaryColor,
                    Position = new Position(0, i * 110),
                    Size = new Size(1920 - DesignTokens.DetailSafeX * 2 - 40, 100),
                    BackgroundColor = focused && !row.P2pUnavailable
                        ? ThemeManager.Current.FocusBgColor
                        : ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Begin,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _listHost.Add(card);
            }

            if (_rows.Count == 0)
            {
                _listHost.Add(new TextLabel
                {
                    Text = "No streams found.",
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
            switch (key)
            {
                case NuvioKey.Up when _focusedIndex > 0:
                    _focusedIndex--;
                    Render();
                    return true;
                case NuvioKey.Down when _focusedIndex < _rows.Count - 1:
                    _focusedIndex++;
                    Render();
                    return true;
                case NuvioKey.Ok when _rows.Count > 0:
                    Activate(_rows[_focusedIndex]);
                    return true;
            }
            return false;
        }

        private void Activate(Row row)
        {
            if (row.P2pUnavailable)
            {
                ShowP2pToast(Core.Localization.I18n.T("native_p2p_unavailable"));
                return;
            }
            if (!string.IsNullOrEmpty(row.Stream.Url) || !string.IsNullOrEmpty(row.Stream.ExternalUrl))
            {
                _ = AppServices.Router.NavigateAsync(Route.Player, new RouteParams()
                    .Set("url", row.Stream.Url ?? row.Stream.ExternalUrl)
                    .Set("title", row.Stream.Name));
            }
        }

        private void ShowP2pToast(string message)
        {
            var toast = new Widgets.Toast();
            Add(toast);
            toast.Show(message);
            _ = Task.Delay(2600).ContinueWith(_ =>
            {
                var parent = toast.GetParent();
                parent?.Remove(toast);
                toast.Dispose();
            });
        }

        public override object ConsumeBackRequest() => null;
    }
}
