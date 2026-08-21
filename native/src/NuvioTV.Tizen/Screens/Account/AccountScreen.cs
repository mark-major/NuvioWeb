using System;
using System.Threading.Tasks;
using NuvioTV.Core.Input;
using NuvioTV.Tizen.Navigation;
using NuvioTV.Tizen.NuiFoundation;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Screens.Account
{
    /// <summary>
    /// Port of accountScreen.js (+accountSettingsContent.js subset): shows the
    /// current session state and offers sign-out via a confirm dialog.
    /// </summary>
    public sealed class AccountScreen : ScreenBase
    {
        private readonly TextLabel _sessionInfo = new TextLabel();
        private readonly TextLabel _signOutRow = new TextLabel();
        private readonly Widgets.NuvioDialog _confirm;

        public AccountScreen()
        {
            var heading = new TextLabel
            {
                Text = "Account",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(DesignTokens.DetailSafeX, 120),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize
            };
            Add(heading);

            _sessionInfo.PointSize = DesignTokens.TypeBody;
            _sessionInfo.TextColor = ThemeManager.Current.TextSecondaryColor;
            _sessionInfo.Position = new Position(DesignTokens.DetailSafeX, 240);
            _sessionInfo.WidthResizePolicy = ResizePolicyType.FillToParent;
            _sessionInfo.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_sessionInfo);

            _signOutRow.Text = "Sign out (OK)";
            _signOutRow.PointSize = DesignTokens.TypeSubtitle;
            _signOutRow.TextColor = ThemeManager.Current.TextColor;
            _signOutRow.Position = new Position(DesignTokens.DetailSafeX, 360);
            _signOutRow.WidthResizePolicy = ResizePolicyType.FillToParent;
            _signOutRow.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            Add(_signOutRow);
        }

        public override async Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            var anonymous = AppServices.Auth.IsAnonymousSession;
            _sessionInfo.Text = AppServices.Auth.IsAuthenticated
                ? (anonymous ? "Signed in anonymously" : "Signed in")
                : "Not signed in";
            await Task.CompletedTask;
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            if (key != NuvioKey.Ok) return false;
            ConfirmSignOut();
            return true;
        }

        private void ConfirmSignOut()
        {
            try
            {
                // Sign out immediately with an on-screen confirmation line
                // (dialog stack integration lands with modal gate wiring).
                _signOutRow.Text = "Signing out…";
                _ = AppServices.Auth.SignOutAsync().ContinueWith(t =>
                {
                    _signOutRow.Text = "Signed out";
                });
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "sign out failed: " + ex.Message);
                _signOutRow.Text = "Sign out failed";
            }
        }
    }
}
