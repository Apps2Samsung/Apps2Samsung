using System;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Install
{
    /// <summary>What stopped an install, as <see cref="PackageInstaller"/> tells the situations apart (#1146).</summary>
    public enum InstallFailure
    {
        /// <summary>Nothing more specific than the TV's own output.</summary>
        Generic,
        /// <summary>The package declares a required Tizen version newer than the TV's; nothing was pushed.</summary>
        NeedsNewerTizen,
        /// <summary>Signing the package failed on this device; nothing was pushed.</summary>
        ResignFailed,
        /// <summary>TV-Store was updated through its own relay and did not come back (#1005).</summary>
        TvStoreNotBack,
        /// <summary>The connection to the TV broke during the push.</summary>
        TransportLost,
        /// <summary>[118, -4]: a newer Tizen is needed, or a privilege the certificate does not grant.</summary>
        OperationNotAllowed,
        /// <summary>[118, -19]: the TV could not read the package's manifest.</summary>
        ManifestUnreadable,
        /// <summary>[116]: no room on the TV.</summary>
        InsufficientSpace,
        /// <summary>[118, -12] "Invalid format of certificate in signature".</summary>
        CertificateInvalidFormat,
        /// <summary>[118, -12], the TV's clock is before the certificate's start date.</summary>
        CertificateNotYetValid,
        /// <summary>[118, -12], the certificate is past its end date by the TV's clock.</summary>
        CertificateExpired,
        /// <summary>[118, -12] "Invalid certificate chain"; thrown as <see cref="CertificateChainRejectedException"/>.</summary>
        CertificateChainRejected,
        /// <summary>[118, -12] with another reason, or none.</summary>
        CertificateRejected,
        /// <summary>The installed copy is signed with a different certificate and was not removed.</summary>
        CertificateMismatch,
        /// <summary>Still a certificate mismatch after the remove-and-retry.</summary>
        CertificateMismatchAfterRemoval,
        /// <summary>[118, -22]: the TV's security manager refuses the package id.</summary>
        PackageIdBlocked,
        /// <summary>A different certificate on the installed copy, and the TV cannot remove apps over the network.</summary>
        CertificateMismatchTvCannotRemove,
        /// <summary>Already installed, overwriting is off, and the TV cannot remove apps over the network.</summary>
        InstalledAndTvCannotRemove,
        /// <summary>Already installed, and both overwriting and removing the installed version are off.</summary>
        InstalledAndRemovalOff,
    }

    /// <summary>
    /// An install that <see cref="PackageInstaller"/> stopped, with the situation as a
    /// <see cref="InstallFailure"/> so a head with a string catalog can say it in its own words
    /// (#1146). The message is the English sentence the installer has always thrown, and the type
    /// derives from <see cref="InvalidOperationException"/>, so a caller that only shows the message
    /// shows what it showed before.
    /// </summary>
    public class InstallFailedException : InvalidOperationException
    {
        public InstallFailedException(InstallFailure failure, string message) : base(message) => Failure = failure;

        public InstallFailure Failure { get; }

        /// <summary>The TV's install output, when the TV was the one to say no.</summary>
        public string? Output { get; init; }

        /// <summary>The TV's own words after "Check certificate error", when it gave any.</summary>
        public string? Reason { get; init; }

        /// <summary>The Tizen version the package asks for, when that is what the failure is about.</summary>
        public string? RequiredTizen { get; init; }

        /// <summary>The Tizen version the TV runs, when known.</summary>
        public string? TvTizen { get; init; }

        /// <summary>The kind a certificate rejection is reported as.</summary>
        public static InstallFailure For(CertificateRejection rejection) => rejection switch
        {
            CertificateRejection.InvalidFormat => InstallFailure.CertificateInvalidFormat,
            CertificateRejection.NotYetValid => InstallFailure.CertificateNotYetValid,
            CertificateRejection.Expired => InstallFailure.CertificateExpired,
            CertificateRejection.InvalidChain => InstallFailure.CertificateChainRejected,
            _ => InstallFailure.CertificateRejected,
        };
    }
}
