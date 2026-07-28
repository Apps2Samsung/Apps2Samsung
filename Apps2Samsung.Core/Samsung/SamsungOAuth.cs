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
    }
}
