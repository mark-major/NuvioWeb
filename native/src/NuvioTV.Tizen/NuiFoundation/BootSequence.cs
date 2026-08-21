using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Configuration;
using NuvioTV.Tizen.Navigation;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Port of js/app.js auth-state routing (Task 18.2): SignedOut → QR gate
    /// (onboarding mode on first launch), Authenticated → profile selection /
    /// home. Update prompt port: GitHub releases/latest check with
    /// parseLatestRelease draft/prerelease filtering.
    /// </summary>
    public static class BootSequence
    {
        private const string GuestQrBypassKey = "skipAuthQrGate";
        private const string HasSeenQrKey = "hasSeenAuthQrOnFirstLaunch";
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/NuvioMedia/NuvioWeb/releases/latest";

        public static async Task RouteAuthStateAsync(AuthState state, Router router)
        {
            switch (state)
            {
                case AuthState.Loading:
                    break;

                case AuthState.SignedOut:
                    if (router.HasCurrent && router.Current == Route.ProfileSelection ||
                        router.HasCurrent && router.Current == Route.AuthQrSignIn)
                    {
                        return; // already on a signed-out route
                    }
                    var hasSeenQr = await Core.Storage.LocalStore.GetAsync(
                        HasSeenQrKey, AppServices.FileStore, false);
                    await router.NavigateAsync(Route.AuthQrSignIn,
                        new RouteParams().Set("onboardingMode", !hasSeenQr),
                        new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
                    break;

                case AuthState.Authenticated:
                    var hasProfiles = true;
                    try
                    {
                        var manager = new Core.Sync.ProfileManager(AppServices.FileStore);
                        hasProfiles = (await manager.GetProfilesAsync())?.Count > 0;
                    }
                    catch
                    {
                        hasProfiles = true;
                    }
                    await router.NavigateAsync(hasProfiles ? Route.Home : Route.ProfileSelection,
                        new RouteParams(),
                        new NavigateOptions { ReplaceHistory = true, SkipStackPush = true });
                    break;
            }
        }

        /// <summary>Update prompt port: GitHub latest release vs current version.</summary>
        public static async Task<string> CheckAppUpdateAsync(string currentVersion)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    using (var response = await AppServices.Http.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var json = await response.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(json))
                        {
                            var root = doc.RootElement;
                            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                            {
                                return null;
                            }
                            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                            {
                                return null;
                            }
                            var tag = root.TryGetProperty("tag_name", out var tagName)
                                ? tagName.GetString() : null;
                            if (!string.IsNullOrEmpty(tag) &&
                                IsNewerVersion(tag, string.IsNullOrEmpty(currentVersion) ? "0.0.0" : currentVersion))
                            {
                                return tag;
                            }
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "update check failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Port of isNewerVersion: numeric part-wise comparison.</summary>
        internal static bool IsNewerVersion(string remoteTag, string localVersion)
        {
            var remote = Normalize(remoteTag);
            var local = Normalize(localVersion);
            for (var i = 0; i < Math.Max(remote.Length, local.Length); i++)
            {
                var r = i < remote.Length ? remote[i] : 0;
                var l = i < local.Length ? local[i] : 0;
                if (r != l) return r > l;
            }
            return false;
        }

        private static int[] Normalize(string version)
        {
            var cleaned = new string((version ?? "").Trim()
                .SkipWhile(c => !char.IsDigit(c)).ToArray());
            if (cleaned.Length == 0) return new[] { 0 };
            return cleaned.Split('.').Select(part =>
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var n) ? n : 0;
            }).ToArray();
        }
    }
}
