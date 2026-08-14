using System;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Interprets the raw output of a Tizen install so both heads agree on what a failure means. The
    /// <b>classification</b> lives here once; each head keeps its own recovery/messaging around it
    /// (the desktop's localized retry state machine, the mobile head's single overwrite retry).
    /// </summary>
    public static class TizenInstallDiagnostics
    {
        private const StringComparison IC = StringComparison.OrdinalIgnoreCase;

        /// <summary>Environmental transport failure (reset/lost connection) — retrying the same push
        /// can't help; it's a VPN/firewall/Wi-Fi issue, not a packaging one.</summary>
        public static bool IsTransportLost(string? output) =>
            Has(output, Constants.TizenErrorCodes.TransportConnectionLost) ||
            Has(output, Constants.TizenErrorCodes.ConnectionResetByPeer);

        /// <summary>Not enough free space on the TV ([116]).</summary>
        public static bool IsInsufficientSpace(string? output) =>
            Has(output, Constants.TizenErrorCodes.DownloadFailed116);

        /// <summary>The installed copy was signed with a different certificate ([118012] / [118, -12]);
        /// Tizen won't overwrite it — the old copy must be removed first.</summary>
        public static bool IsCertificateMismatch(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118012) ||
            Has(output, Constants.TizenErrorCodes.InstallFailed118Minus12);

        /// <summary>The TV refused the package with [118, -4] ("operation not allowed" / "load archive
        /// info fail"). Ambiguous: usually the package targets a newer Tizen than the TV, OR it needs
        /// Partner signing / a privilege the certificate doesn't grant — so don't present it as a flat
        /// "TV too old" (misleading when other apps install fine). Retrying/overwriting won't help.</summary>
        public static bool IsApiVersionMismatch(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118Minus4);

        /// <summary>A package-id / config conflict ([118], not the more specific variants above).</summary>
        public static bool IsPackageIdConflict(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118);

        /// <summary>A generic reported failure.</summary>
        public static bool IsGenericFailure(string? output) =>
            Has(output, Constants.TizenErrorCodes.Failed);

        /// <summary>The output indicates the install actually succeeded.</summary>
        public static bool IndicatesSuccess(string? output) =>
            Has(output, Constants.TizenErrorCodes.Installing100) ||
            Has(output, Constants.TizenErrorCodes.InstallCompleted);

        /// <summary>
        /// The output reports the install actually FAILED — regardless of the process exit code.
        /// Tizen's install helper returns process exit 0 (<c>cmd_ret:0</c>) even when the app install
        /// itself failed (e.g. <c>app_id[...] install failed[118]</c>), so a caller must not treat a
        /// zero exit code as success on its own — it has to classify the output. Matches any
        /// <c>install failed[...]</c> code, a <c>val[fail]</c> result marker, and the transport /
        /// out-of-space signals. Deliberately does NOT match a bare "failed" substring, so an
        /// unrelated line (e.g. "Pull skipped or failed") can't be mistaken for an install failure.
        /// </summary>
        public static bool IndicatesFailure(string? output) =>
            IsTransportLost(output) ||
            IsInsufficientSpace(output) ||
            Has(output, "install failed[") ||
            Has(output, "val[fail]");

        private static bool Has(string? output, string token) =>
            !string.IsNullOrEmpty(output) && output.Contains(token, IC);
    }
}
