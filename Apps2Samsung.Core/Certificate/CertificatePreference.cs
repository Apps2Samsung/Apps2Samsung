using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// The Settings > Installing certificate choice, the same three on both heads: Automatic,
    /// Public or Partner. Stored as these strings (the mobile head's preference values since #755).
    /// </summary>
    public static class CertificatePreference
    {
        public const string Automatic = "Automatic";
        public const string Public = "Public";
        public const string Partner = "Partner";

        /// <summary>One of the three, Automatic for anything else (a missing or unknown value).</summary>
        public static string Normalize(string? preference) => preference switch
        {
            Public => Public,
            Partner => Partner,
            _ => Automatic,
        };

        /// <summary>
        /// The level an install signs with:
        ///  • Partner   → always Partner.
        ///  • Public    → Public, unless the package requires Partner: a hard requirement wins over
        ///                the preference, because a Public-signed install would simply be refused.
        ///  • Automatic → Public, unless the package requires Partner.
        /// Partner is only needed by apps that use restricted privileges (e.g. vpnservice, drminfo).
        /// </summary>
        public static CertificatePrivilegeLevel ResolveLevel(string? preference, bool requirePartner) =>
            Normalize(preference) == Partner || requirePartner
                ? CertificatePrivilegeLevel.Partner
                : CertificatePrivilegeLevel.Public;

        /// <summary>
        /// True when an install is the stock Jellyfin build going to a TV newer than the push
        /// installs and older than the Tizen that insists on a Samsung certificate, exactly as it
        /// was built: no server address patched in, no fresh certificate asked for, and no edit by
        /// this run (a new package id, a custom icon; #1143). That build is made with the shipped
        /// certificate and is signed with it again, whatever the certificate settings say, so a TV
        /// that already has it keeps seeing the same author (#1148).
        /// </summary>
        public static bool StockBuildKeepsShippedCertificate(Version tizenVersion, string packageFileName, bool serverConfigured, bool forceLogin, bool packageEdited) =>
            tizenVersion > new Version(Apps2Samsung.Helpers.Core.Constants.TizenVersions.PushInstallMax) &&
            tizenVersion < new Version(Apps2Samsung.Helpers.Core.Constants.TizenVersions.CertificateRequired) &&
            !serverConfigured &&
            !forceLogin &&
            !packageEdited &&
            packageFileName.Contains(Apps2Samsung.Helpers.Core.Constants.AppIdentifiers.JellyfinAppName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when an install may sign with the shipped certificate instead of a Samsung one: the TV
        /// is newer than the push installs and older than the Tizen that insists on a Samsung
        /// certificate, and the install signs as Public. The shipped certificate is Public only, so
        /// a Partner choice or a package that needs Partner always gets a Samsung certificate (#1107).
        /// </summary>
        public static bool ShippedCertificateApplies(Version tizenVersion, string? preference, bool requirePartner) =>
            tizenVersion > new Version(Apps2Samsung.Helpers.Core.Constants.TizenVersions.PushInstallMax) &&
            tizenVersion < new Version(Apps2Samsung.Helpers.Core.Constants.TizenVersions.CertificateRequired) &&
            ResolveLevel(preference, requirePartner) == CertificatePrivilegeLevel.Public;

        /// <summary>
        /// True when the package can only be installed Partner-signed and there is no Partner
        /// certificate yet: the caller switches the preference to Partner, so one is actually created
        /// and Settings shows the level the app is signed with. The user can switch back afterwards.
        /// </summary>
        public static bool ShouldSwitchToPartner(string? preference, bool requirePartner, bool hasPartnerProfile) =>
            requirePartner && Normalize(preference) != Partner && !hasPartnerProfile;

        /// <summary>
        /// The preference after a backup import: the imported level when exactly one of the two was
        /// in the backup, otherwise <paramref name="current"/> (Automatic covers "both").
        /// </summary>
        public static string AfterImport(string? current, bool hasPublic, bool hasPartner) =>
            hasPartner && !hasPublic ? Partner
            : hasPublic && !hasPartner ? Public
            : Normalize(current);
    }
}
