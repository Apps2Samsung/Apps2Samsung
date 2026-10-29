using System;

namespace Apps2Samsung.Samsung
{
    /// <summary>
    /// Samsung account OAuth endpoint + parameters, shared by every head so the authorize URL and
    /// loopback callback stay identical. Mirrors the desktop's <c>Constants.Samsung</c> values.
    /// Note: SignInGate returns the token by <b>POST</b>ing a URL-encoded JSON body to the
    /// redirect_uri (not a GET <c>?code=</c>), so the callback must read the request body.
    /// </summary>
    public static class SamsungOAuth
    {
        public const string SignInGateUrl = "https://account.samsung.com/accounts/be1dce529476c1a6d407c4c7578c31bd/signInGate";
        public const string ClientId = "v285zxnl3h";
        public const string State = "accountcheckdogeneratedstatetext";
        public const string TokenType = "TOKEN";

        /// <summary>Loopback port the callback listener binds to (matches the desktop Kestrel callback).</summary>
        public const int CallbackPort = 4794;
        public const string CallbackPath = "/signin/callback";

        /// <summary>The redirect_uri every head registers: the loopback listener above. Also what the
        /// desktop's "Copy sign-in link" hands the user, so a sign-in pasted into another browser still
        /// lands on the same listener.</summary>
        public static readonly string LoopbackRedirectUri = $"http://localhost:{CallbackPort}{CallbackPath}";

        /// <summary>Builds the SignInGate authorize URL for the given loopback <paramref name="redirectUri"/>.</summary>
        public static string BuildAuthorizeUrl(string redirectUri) =>
            $"{SignInGateUrl}?locale=&clientId={ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={State}&tokenType={TokenType}";

        /// <summary>
        /// True when the <c>state</c> returned on the callback matches the value we sent in
        /// <see cref="BuildAuthorizeUrl"/>. The single implementation both heads use to reject a
        /// callback whose state doesn't round-trip (CSRF / stray callback protection).
        /// </summary>
        public static bool IsValidState(string? state) =>
            string.Equals(state, State, StringComparison.Ordinal);

        /// <summary>
        /// True when <paramref name="url"/> is Google's sign-in page, where SignInGate's "Sign in with
        /// Google" button leads. Google refuses to serve that page to an embedded WebView
        /// (<c>disallowed_useragent</c>), and Samsung only delivers the token to the loopback callback
        /// from inside one (a real browser blocks the https → localhost POST), so on the mobile heads
        /// that button can't work either way. The WebView hosts stop the navigation here and tell the
        /// user to add a password to the Samsung account instead (#807).
        /// </summary>
        public static bool IsGoogleSignIn(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase);
    }
}
