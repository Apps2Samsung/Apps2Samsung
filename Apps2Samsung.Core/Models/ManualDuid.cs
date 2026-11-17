using System;
using System.Linq;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// The DUIDs the user typed into Settings, for TVs that cannot be read over SDB (#979): a Host PC
    /// IP that points elsewhere, or a firmware that closes the channel instead of answering
    /// <c>0 getduid</c>. Shared by the CLI and the phone so both apply the same rule.
    /// </summary>
    public static class ManualDuid
    {
        private static readonly char[] Separators = { ',', ';', ' ', '\n', '\r' };

        /// <summary>Splits the setting into the individual DUIDs it lists.</summary>
        public static string[] Split(string? value) =>
            (value ?? string.Empty)
                .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>
        /// The one usable DUID from the setting, or null. Only when exactly one is configured: this
        /// setting also lists extra TVs to authorize in the certificate, so with two or more entries
        /// there is no way to tell which one is the TV being installed to, and guessing would build a
        /// certificate for the wrong device and fail later with a less clear error.
        /// </summary>
        public static string? Only(string? value)
        {
            var split = Split(value);
            return split.Length == 1 && TizenDuid.IsValid(split[0]) ? split[0].Trim() : null;
        }
    }
}
