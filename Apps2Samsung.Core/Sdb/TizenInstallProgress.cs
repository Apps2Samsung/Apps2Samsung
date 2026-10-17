using System;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Reads the percentage off the TV's own install log. While <c>0 vd_appinstall</c> runs, the set
    /// reports one line per step — <c>app_id[AprZAARz4r.Jellyfin] installing[9]</c>, <c>installing[19]</c>,
    /// … <c>installing[100]</c> — and that number is what the heads show as a progress bar. The lines
    /// arrive through the console capture in <see cref="InProcessSdbEngine"/>; nothing here talks to
    /// the TV.
    /// </summary>
    public static class TizenInstallProgress
    {
        private static readonly Regex Percent = new(
            @"installing\[(\d{1,3})\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True when <paramref name="line"/> is one of the TV's <c>installing[N]</c> reports; the
        /// percentage comes back clamped to 0–100.
        /// </summary>
        public static bool TryParsePercent(string? line, out int percent)
        {
            percent = 0;
            if (string.IsNullOrEmpty(line))
                return false;

            var match = Percent.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var value))
                return false;

            percent = Math.Clamp(value, 0, 100);
            return true;
        }
    }
}
