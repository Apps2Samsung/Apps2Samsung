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
    }
}
