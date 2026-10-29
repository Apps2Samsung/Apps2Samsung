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

        /// <summary>Environmental transport failure (reset/lost connection), retrying the same push
        /// can't help; it's a VPN/firewall/Wi-Fi issue, not a packaging one.</summary>
        public static bool IsTransportLost(string? output) =>
            Has(output, Constants.TizenErrorCodes.TransportConnectionLost) ||
            Has(output, Constants.TizenErrorCodes.ConnectionResetByPeer);

        /// <summary>Not enough free space on the TV ([116]).</summary>
        public static bool IsInsufficientSpace(string? output) =>
            Has(output, Constants.TizenErrorCodes.DownloadFailed116);

        /// <summary>The installed copy was signed with a different certificate ([118012] / [118, -12],
        /// "Package with the same id and different certificate has been already installed"); Tizen won't
        /// overwrite it, so the old copy must be removed first. A [118, -12] whose reason is the TV
        /// rejecting the certificate itself is <b>not</b> this (see <see cref="GetCertificateRejection"/>):
        /// there is nothing to remove, and the prompt to do so sent a user chasing an app that was never
        /// installed (#907).</summary>
        public static bool IsCertificateMismatch(string? output) =>
            IsCertificateFailure(output) && GetCertificateRejection(output) == CertificateRejection.None;

        /// <summary>
        /// The TV refused the signing certificate itself ([118, -12] with a "Check certificate error"
        /// reason other than the same-id-different-certificate one). Which kind decides the advice:
        /// a TV clock that is off (a set without internet or an antenna signal has no time source,
        /// #907) or a certificate past its dates. <see cref="CertificateRejection.None"/> when the
        /// output is not a [118, -12] at all, carries no reason (older sets), or names the installed
        /// copy, i.e. the <see cref="IsCertificateMismatch"/> case.
        /// </summary>
        public static CertificateRejection GetCertificateRejection(string? output)
        {
            if (!IsCertificateFailure(output) || !Has(output, Constants.TizenErrorCodes.CheckCertificateError))
                return CertificateRejection.None;
            if (Has(output, Constants.TizenErrorCodes.CertificateSameIdDifferent))
                return CertificateRejection.None;
            if (Has(output, Constants.TizenErrorCodes.CertificateInvalidFormat))
                return CertificateRejection.InvalidFormat;
            if (Has(output, Constants.TizenErrorCodes.CertificateNotYetValid))
                return CertificateRejection.NotYetValid;
            if (Has(output, Constants.TizenErrorCodes.CertificateExpired))
                return CertificateRejection.Expired;
            if (Has(output, Constants.TizenErrorCodes.CertificateInvalidChain))
                return CertificateRejection.InvalidChain;
            return CertificateRejection.Other;
        }

        /// <summary>The validator's own words for a rejection, "Invalid format of certificate in
        /// signature." for <c>reason: Check certificate error : :Invalid format of certificate in
        /// signature.:&lt;-2&gt;</c>; the whole reason when the shape differs, null without one.</summary>
        public static string? CertificateRejectionReason(string? output)
        {
            if (string.IsNullOrEmpty(output))
                return null;
            var at = output.IndexOf(Constants.TizenErrorCodes.CheckCertificateError, IC);
            if (at < 0)
                return null;
            var line = output.Substring(at);
            var end = line.IndexOfAny(new[] { '\r', '\n' });
            if (end >= 0)
                line = line.Substring(0, end);
            // "Check certificate error : :<text>:<code>" -> <text>
            var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length >= 2 ? parts[1] : line.Trim();
        }

        private static bool IsCertificateFailure(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118012) ||
            Has(output, Constants.TizenErrorCodes.InstallFailed118Minus12);

        /// <summary>The TV refused the package with [118, -4] ("operation not allowed" / "load archive
        /// info fail"). Ambiguous: usually the package targets a newer Tizen than the TV, OR it needs
        /// Partner signing / a privilege the certificate doesn't grant, so don't present it as a flat
        /// "TV too old" (misleading when other apps install fine). Retrying/overwriting won't help.</summary>
        public static bool IsApiVersionMismatch(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118Minus4);

        /// <summary>
        /// The TV's security manager rejected the package ([118, -22], or [118022] on Tizen 4 / "Security
        /// error : :Invalid function parameter was given:&lt;2&gt;", SECURITY_MANAGER_ERROR_INPUT_PARAM).
        /// In practice this means the package <b>id</b> is blocked on this set: it is still bound to a
        /// different author certificate, or a broken uninstall left a record behind that the app list no
        /// longer shows, so there is nothing for us to remove over SDB (#702, #422). Re-signing and
        /// pushing the same file again cannot clear it, only deleting the old copy on the TV itself,
        /// installing a build with a different package id, or a Smart Hub reset can.
        /// <para>Checked before <see cref="IsPackageIdConflict"/>: the text is "install failed[118, -22]",
        /// which does not contain the plain "install failed[118]" token either way, so without this it
        /// fell through to the generic branch.</para>
        /// </summary>
        public static bool IsPackageIdBlocked(string? output) =>
            Has(output, Constants.TizenErrorCodes.InstallFailed118Minus22) ||
            Has(output, Constants.TizenErrorCodes.InstallFailed118022);

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
        /// The output reports the install actually FAILED, regardless of the process exit code.
        /// Tizen's install helper returns process exit 0 (<c>cmd_ret:0</c>) even when the app install
        /// itself failed (e.g. <c>app_id[...] install failed[118]</c>), so a caller must not treat a
        /// zero exit code as success on its own, it has to classify the output. Matches any
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

    /// <summary>Why the TV rejected the signing certificate, from the [118, -12] reason text.</summary>
    public enum CertificateRejection
    {
        /// <summary>Not a rejection of the certificate: no [118, -12], no reason, or the installed-copy case.</summary>
        None,
        /// <summary>Tizen &lt;-2&gt; "Invalid format of certificate in signature": the chain didn't verify on the TV (#907).</summary>
        InvalidFormat,
        /// <summary>Tizen &lt;-7&gt; "Certificate in signature is not valid yet": the TV's clock is behind the certificate's start.</summary>
        NotYetValid,
        /// <summary>Tizen &lt;-6&gt; "Certificate in signature is expired": past its end date, or the TV's clock is ahead.</summary>
        Expired,
        /// <summary>Tizen &lt;-3&gt; "Invalid certificate chain in signature".</summary>
        InvalidChain,
        /// <summary>Another "Check certificate error" text.</summary>
        Other,
    }
}
