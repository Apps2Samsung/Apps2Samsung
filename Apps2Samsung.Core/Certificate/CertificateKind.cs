using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// Which certificate an install signs with (#988), the Settings > Installing choice above the
    /// level switch on both heads: a Samsung one, made for the TV with a Samsung account, or a Tizen
    /// one, the SDK's, made on this device with no account and no device ID. Stored as these strings.
    /// </summary>
    public static class CertificateKind
    {
        public const string Samsung = "Samsung";
        public const string Tizen = "Tizen";

        /// <summary>One of the two, Samsung for anything else (a missing or unknown value).</summary>
        public static string Normalize(string? kind) => kind == Tizen ? Tizen : Samsung;

        public static bool IsTizen(string? kind) => Normalize(kind) == Tizen;

        /// <summary>
        /// The Tizen level the Automatic / Public / Partner choice means, by the same rule as
        /// <see cref="CertificatePreference.ResolveLevel"/>: Partner when chosen or required, else
        /// Public. Platform is never picked here; it only exists on the CLI, by name.
        /// </summary>
        public static TizenDistributorLevel TizenLevel(string? preference, bool requirePartner) =>
            CertificatePreference.ResolveLevel(preference, requirePartner) == CertificatePrivilegeLevel.Partner
                ? TizenDistributorLevel.Partner
                : TizenDistributorLevel.Public;
    }
}
