namespace NuvioTV.Core.Auth
{
    /// <summary>
    /// Auth state machine values. JS parity: js/core/auth/authState.js
    /// (LOADING / SIGNED_OUT / AUTHENTICATED).
    /// </summary>
    public enum AuthState
    {
        Loading,
        SignedOut,
        Authenticated
    }
}
