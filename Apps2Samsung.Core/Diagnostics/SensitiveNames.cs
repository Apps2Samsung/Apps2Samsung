using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>
    /// Which names carry a credential, and what to put in place of one. Shared by everything in the
    /// console that shows data the TV hands back — a diagnostics body, a request URL — so a transcript
    /// stays safe to paste into an issue whichever of them produced the line.
    /// </summary>
    public static class SensitiveNames
    {
        /// <summary>What a masked value is replaced with, in JSON, in a query string and in plain text alike.</summary>
        public const string MaskedValue = "••• masked •••";

        // Matched anywhere in the name, case-insensitively, so authToken, X-Api-Key and refresh_secret
        // are all covered. Deliberately narrow around "session": a session id is a credential, a
        // session count is a diagnostic.
        public const string Pattern =
            "token|password|passwd|secret|api[-_ ]?key|apikey|authoriz|credential|cookie|passphrase|" +
            "private[-_ ]?key|session[-_ ]?(?:id|key|token)";

        private static readonly Regex Name = new(Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Short names that only mean a credential in a query string: as a JSON property "auth" or
        // "sig" could be anything, but ?sig=… is a signed URL every time.
        private static readonly string[] QueryOnly = ["sig", "signature", "hmac", "auth", "key"];

        /// <summary>Whether a property, header or field name names a credential.</summary>
        public static bool Matches(string? name) => !string.IsNullOrEmpty(name) && Name.IsMatch(name);

        /// <summary>Whether a query parameter's name names a credential.</summary>
        public static bool MatchesQueryName(string? name) =>
            Matches(name) || QueryOnly.Contains(name, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Masks the values of query parameters that name a credential, leaving the rest of the URL as
        /// it is — the path is what identifies the request, and an api_key in the query is exactly what
        /// should not travel with it into an issue.
        /// </summary>
        public static string MaskQuery(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            var query = url.IndexOf('?');
            if (query < 0 || query == url.Length - 1)
                return url;

            // Split the fragment off first: it is not part of the query, and a token rarely lives there.
            var tail = url[(query + 1)..];
            var fragment = string.Empty;
            var hash = tail.IndexOf('#');
            if (hash >= 0)
            {
                fragment = tail[hash..];
                tail = tail[..hash];
            }

            var masked = tail.Split('&').Select(pair =>
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                    return pair;

                var name = pair[..equals];
                return MatchesQueryName(name) ? $"{name}={MaskedValue}" : pair;
            });

            return url[..(query + 1)] + string.Join("&", masked) + fragment;
        }
    }
}
