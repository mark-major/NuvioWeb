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
    /// Port of authSignInScreen.js: email/password text entry via remote-key
    /// typing and an Ok submit that calls AuthManager.SignInWithEmailAsync.
    /// </summary>
    public sealed class AuthSignInScreen : ScreenBase
    {
        private readonly Widgets.SearchField _emailField = new Widgets.SearchField("Email");
        private readonly Widgets.SearchField _passwordField = new Widgets.SearchField("Password");
        private readonly TextLabel _status = new TextLabel();
        private readonly TextLabel[] _rows = new TextLabel[3];
        private int _focusedRow; // 0 = email, 1 = password, 2 = sign in
        private string _password = "";

        public bool PasswordMode
        {
            get { return _focusedRow == 1 && _typing; }
        }

        private bool _typing;

        public AuthSignInScreen()
        {
            var heading = new TextLabel
            {
                Text = "Sign in with email",
                PointSize = DesignTokens.TypeTitle,
                TextColor = ThemeManager.Current.TextColor,
                Position = new Position(0, 120),
                WidthResizePolicy = ResizePolicyType.FillToParent,
                HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Add(heading);

            _emailField.Position = new Position((1920 - 1200) / 2f, 320);
            Add(_emailField);

            _passwordField.Position = new Position((1920 - 1200) / 2f, 440);
            Add(_passwordField);

            for (var i = 0; i < _rows.Length; i++)
            {
                var row = new TextLabel
                {
                    PointSize = DesignTokens.TypeBody,
                    WidthResizePolicy = ResizePolicyType.FillToParent,
                    HeightResizePolicy = ResizePolicyType.UseNaturalSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Position = new Position(0, 560 + i * 60)
                };
                _rows[i] = row;
                Add(row);
            }
            _rows[0].TextColor = ThemeManager.Current.TextColor;
            _rows[1].TextColor = ThemeManager.Current.TextSecondaryColor;
            _rows[2].TextColor = ThemeManager.Current.FocusColorValue;
            UpdateRows();

            _status.PointSize = DesignTokens.TypeBody;
            _status.TextColor = ThemeManager.Current.TextSecondaryColor;
            _status.Position = new Position(0, 800);
            _status.WidthResizePolicy = ResizePolicyType.FillToParent;
            _status.HeightResizePolicy = ResizePolicyType.UseNaturalSize;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            Add(_status);
        }

        public override Task MountAsync(RouteParams p, NavigationContext ctx)
        {
            _status.Text = "";
            return Task.CompletedTask;
        }

        private void UpdateRows()
        {
            _rows[0].Text = (_focusedRow == 0 ? "› " : "") + "Email: " + _emailField.Text;
            _rows[1].Text = (_focusedRow == 1 ? "› " : "") + "Password: " + new string('•', _password.Length);
            _rows[2].Text = (_focusedRow == 2 ? "› " : "") + "Sign in (OK)";
        }

        public override bool OnKeyDown(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up:
                    if (_focusedRow > 0) { _focusedRow--; UpdateRows(); }
                    return true;
                case NuvioKey.Down:
                    if (_focusedRow < 2) { _focusedRow++; UpdateRows(); }
                    return true;
                case NuvioKey.Back:
                    // Backspace semantics inside a row.
                    DeleteChar();
                    return true;
                case NuvioKey.Ok:
                    if (_focusedRow == 2)
                    {
                        _ = SignInAsync();
                        return true;
                    }
                    return false;
                default:
                    // Letter/digit keys type into the focused row.
                    return HandleCharacter(key);
            }
        }

        private bool HandleCharacter(NuvioKey key)
        {
            char? c = null;
            switch (key)
            {
                case NuvioKey.LetterS: c = 's'; break;
                case NuvioKey.LetterT: c = 't'; break;
                case NuvioKey.LetterC: c = 'c'; break;
                case NuvioKey.LetterE: c = 'e'; break;
                case NuvioKey.LetterP: c = 'p'; break;
                case NuvioKey.LetterB: c = 'b'; break;
                case NuvioKey.LetterL: c = 'l'; break;
            }
            if (c == null)
            {
                return false;
            }

            if (_focusedRow == 0)
            {
                _emailField.SetText(_emailField.Text + c);
            }
            else if (_focusedRow == 1)
            {
                _password += c;
            }
            UpdateRows();
            return true;
        }

        private void DeleteChar()
        {
            if (_focusedRow == 0)
            {
                var t = _emailField.Text;
                if (t.Length > 0) _emailField.SetText(t.Substring(0, t.Length - 1));
            }
            else if (_focusedRow == 1 && _password.Length > 0)
            {
                _password = _password.Substring(0, _password.Length - 1);
            }
            UpdateRows();
        }

        private async Task SignInAsync()
        {
            try
            {
                _status.Text = "Signing in…";
                await AppServices.Auth.SignInWithEmailAsync(_emailField.Text, _password);
                _status.Text = AppServices.Auth.IsAuthenticated ? "Signed in!" : "Sign-in failed.";
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "email signin failed: " + ex.Message);
                _status.Text = "Sign-in failed. Check credentials.";
            }
        }

        public override void Cleanup()
        {
            _emailField.Clear();
            _password = "";
            UpdateRows();
        }
    }
}
