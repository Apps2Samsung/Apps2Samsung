using System;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Sdb
{
    /// <summary>What the TV's launcher said about a <c>0 was_execute</c>.</summary>
    public enum TizenLaunchVerdict
    {
        /// <summary>The reply carries none of the launcher's markers — an old firmware's terse line, or not a reply from the TV at all.</summary>
        Unknown,

        /// <summary>The launcher reported the app started (<c>app_id[…] launched</c>).</summary>
        Launched,

        /// <summary>
        /// <c>launch failed[400]</c>: the id is not in the Smart Hub app database. That is what every
        /// platform app answers — the hotel menu, the factory menu, the store — because
        /// <c>was_execute</c> is the Smart Hub launcher and only resolves Smart Hub apps
        /// (tizen-community-packages#34). A sideloaded app is a Smart Hub app; the TV's own menus are
        /// not. No retry over any other route changes this.
        /// </summary>
        NotASmartHubApp,

        /// <summary>The TV refused for another reason it named — a different failure code, "denied", or sdbd's one-word <c>closed</c>.</summary>
        Refused,
    }

    /// <summary>
    /// Reads the launcher's reply to <c>0 was_execute</c>. On current firmware it is three lines:
    /// <c>launch app &lt;id&gt;</c>, <c>app_id[&lt;smart hub id&gt;] launch start</c>,
    /// <c>app_id[…] launched</c>; a refusal is <c>app_id[&lt;id&gt;] launch failed[&lt;code&gt;]</c>.
    /// Older sets answer with less, or nothing.
    /// </summary>
    public static class TizenLaunchReply
    {
        private static readonly Regex Failed = new(@"launch failed\[(?<code>\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Launched = new(@"\blaunched\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Kept narrow on purpose: the app id is echoed back in every reply, so anything broader than
        // these would trip over an id containing e.g. "error". "closed" is sdbd's whole answer to a
        // verb it does not whitelist.
        private static readonly string[] RefusalMarkers =
            { "fail", "denied", "not permitted", "no such", "not exist", "not found", "closed" };

        /// <summary>The launcher's own text starts like this; a transport error message does not.</summary>
        public static bool IsFromLauncher(string? reply) =>
            !string.IsNullOrWhiteSpace(reply) &&
            (reply.Contains("app_id[", StringComparison.OrdinalIgnoreCase) ||
             reply.Contains("launch app", StringComparison.OrdinalIgnoreCase));

        public static TizenLaunchVerdict Parse(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return TizenLaunchVerdict.Unknown;

            var failed = Failed.Match(reply);
            if (failed.Success)
                return failed.Groups["code"].Value == "400" ? TizenLaunchVerdict.NotASmartHubApp : TizenLaunchVerdict.Refused;

            if (Launched.IsMatch(reply))
                return TizenLaunchVerdict.Launched;

            foreach (var marker in RefusalMarkers)
            {
                if (reply.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return TizenLaunchVerdict.Refused;
            }

            return TizenLaunchVerdict.Unknown;
        }

        /// <summary>
        /// The launcher's lines only, for showing a user what the TV said — without the
        /// <c>launch app &lt;id&gt;</c> echo that every reply opens with.
        /// </summary>
        public static string Summarize(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return string.Empty;

            var lines = reply.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kept = Array.FindAll(lines, l => !l.StartsWith("launch app ", StringComparison.OrdinalIgnoreCase));
            return string.Join(" · ", kept.Length > 0 ? kept : lines);
        }
    }
}
