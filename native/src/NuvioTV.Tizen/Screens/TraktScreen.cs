using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens
{
    /// <summary>
    /// Port of traktScreen.js core flows: device-code sign-in (UserCode +
    /// verification URL + poll loop), then stats summary and a watchlist rail.
    /// </summary>
    public sealed class TraktScreen : ScreenBase
    {
        private readonly Core.Integrations.Trakt.TraktClient _trakt =
            new Core.Integrations.Trakt.TraktClient(AppServices.Http,
                new Platform.TraktAuthStateStore(AppServices.FileStore));

        private readonly TextLabel _status = new TextLabel();
        private readonly TextLabel _stats = new TextLabel();
        private readonly View _listHost = new View();

        public TraktScreen()
        {
            var heading = new TextLabel
            {
                Text = "Trakt",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.DetailSafeX, 60),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(DesignTokens.DetailSafeX, 160);
            _status.Size = new Size(900, 200);
            _status.MultiLine = true;
            Add(_status);

            _stats.PointSize = DesignTokens.TypeSecondary;
            _stats.TextColor = ThemeManager.Current.TextColor;
            _stats.Position = new Position(DesignTokens.DetailSafeX, 400);
            _stats.Size = new Size(1200, 200);
            _stats.MultiLine = true;
            Add(_stats);

            _listHost.Position = new Position(DesignTokens.DetailSafeX, 620);
            _listHost.Size = new Size(1920 - DesignTokens.DetailSafeX * 2, 380);
            Add(_listHost);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            if (await _trakt.GetValidAccessTokenAsync() != null)
            {
                _status.Text = "Signed in to Trakt.";
                await LoadStatsAndWatchlistAsync();
                return;
            }

            try
            {
                var deviceCode = await _trakt.StartDeviceAuthAsync();
                if (deviceCode == null)
                {
                    _status.Text = "Could not start device sign-in. Press OK to retry.";
                    return;
                }
                _status.Text = $"Open {deviceCode.VerificationUrl}\nand enter code: {deviceCode.UserCode}";
                RenderWatchlist(new List<string>());

                // Poll loop (bounded by expiry) on a background task.
                _ = Task.Run(async () =>
                {
                    while (true)
                    {
                        var result = await _trakt.PollDeviceTokenAsync();
                        if (result.Type == Core.Integrations.Trakt.TraktPollResultType.Approved ||
                            result.Type == Core.Integrations.Trakt.TraktPollResultType.Expired)
                        {
                            if (result.Type == Core.Integrations.Trakt.TraktPollResultType.Approved)
                            {
                                await ReloadAfterSignInAsync();
                            }
                            else
                            {
                                _status.Text = "Device code expired. Press OK to retry.";
                            }
                            return;
                        }
                        await Task.Delay(Math.Max(1, result.PollIntervalSeconds ?? 5) * 1000);
                    }
                });
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "trakt device auth failed: " + ex.Message);
                _status.Text = "Sign-in failed. Press OK to retry.";
            }
        }

        private async Task ReloadAfterSignInAsync()
        {
            _status.Text = "Signed in!";
            await LoadStatsAndWatchlistAsync();
        }

        private async Task LoadStatsAndWatchlistAsync()
        {
            try
            {
                var stats = await _trakt.FetchStatsAsync();
                if (stats.HasValue)
                {
                    var s = stats.Value;
                    _stats.Text =
                        $"Movies: {GetInt(s, "movies")}   Shows: {GetInt(s, "shows")}   Episodes: {GetInt(s, "episodes")}";
                }

                var watchlist = await _trakt.FetchWatchlistAsync(20);
                var names = new List<string>();
                foreach (var entry in watchlist)
                {
                    string title = null;
                    if (entry.TryGetProperty("movie", out var movie) &&
                        movie.TryGetProperty("title", out var t))
                    {
                        title = t.GetString();
                    }
                    else if (entry.TryGetProperty("show", out var show) &&
                             show.TryGetProperty("title", out var st))
                    {
                        title = st.GetString();
                    }
                    if (!string.IsNullOrEmpty(title))
                    {
                        names.Add(title);
                    }
                }
                RenderWatchlist(names);
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "trakt load failed: " + ex.Message);
                _stats.Text = "Couldn't load Trakt data.";
            }
        }

        private static int GetInt(JsonElement element, string name)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value) &&
                value.TryGetProperty("count", out var count) &&
                count.TryGetInt32(out var parsed))
            {
                return parsed;
            }
            return 0;
        }

        private void RenderWatchlist(IReadOnlyList<string> names)
        {
            while (_listHost.ChildCount > 0)
            {
                var child = _listHost.GetChildAt(0);
                _listHost.Remove(child);
                child.Dispose();
            }
            for (var i = 0; i < Math.Min(names.Count, 8); i++)
            {
                _listHost.Add(new TextLabel
                {
                    Text = names[i],
                    PointSize = DesignTokens.TypeBody,
                    TextColor = ThemeManager.Current.TextColor,
                    Position = new Position((i % 4) * 400, (i / 4) * 150),
                    Size = new Size(380, 130),
                    BackgroundColor = ThemeManager.Current.CardBgColor,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (key == NuvioKey.Ok && !_status.Text.Contains("Signed in"))
            {
                _ = MountAsync(new RouteParams(), null);
                return true;
            }
            return false;
        }

        public override object ConsumeBackRequest() => null;
    }
}
