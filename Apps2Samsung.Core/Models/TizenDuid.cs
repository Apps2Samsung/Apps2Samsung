using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// Validates a Tizen device DUID. A real DUID is a run of letters/digits (e.g. "2DCKJITTLDPSA");
    /// a failed SDB read returns human-readable error text (spaces, colons) instead. Guarding on the
    /// shape stops that error text from being embedded in a certificate's device-id SAN.
    /// </summary>
    public static class TizenDuid
    {
        private static readonly Regex Valid = new(@"^[A-Za-z0-9]{10,64}$", RegexOptions.Compiled);

        public static bool IsValid(string? duid) =>
            !string.IsNullOrWhiteSpace(duid) && Valid.IsMatch(duid.Trim());

        /// <summary>
        /// Picks the DUID out of a <c>0 getduid</c> reply: the first line shaped like one, or empty
        /// when there is none. The reply is not always the DUID alone: a 2024 Tizen 8.0 set puts
        /// "Failed open device: Operation not permitted" on a line of its own in front of it (#1081),
        /// and taking the first line then threw away a perfectly good read.
        /// </summary>
        public static string FromSdbOutput(string? output) =>
            Lines(output).FirstOrDefault(IsValid) ?? string.Empty;

        /// <summary>The first non-blank line of a reply, for saying what came back instead of a DUID.</summary>
        public static string FirstLine(string? output) =>
            Lines(output).FirstOrDefault() ?? string.Empty;

        private static string[] Lines(string? output) =>
            (output ?? string.Empty).Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
