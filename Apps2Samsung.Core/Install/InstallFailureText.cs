using System;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Install
{
    /// <summary>
    /// An <see cref="InstallFailedException"/> in the words of a head's string catalog (#1149): the
    /// en.json key each <see cref="InstallFailure"/> has always had on the desktop, formatted with
    /// what the failure carries. A kind without a key of its own keeps the installer's sentence.
    /// </summary>
    public static class InstallFailureText
    {
        /// <param name="appTitle">The app's name as the user knows it, for the texts that name it.</param>
        /// <param name="localize">The catalog lookup: a key in, its text out.</param>
        public static string Describe(InstallFailedException failure, string appTitle, Func<string, string> localize)
        {
            string Text(string key) => localize(key);
            string Format(string key, params object?[] args) => string.Format(localize(key), args);
            string Failed(string detail) => Format("statusInstallationFailedDetail", detail);

            return failure.Failure switch
            {
                InstallFailure.NeedsNewerTizen =>
                    Format(Constants.LocalizationKeys.IncompatiblePackageDetailed, failure.RequiredTizen, failure.TvTizen),
                InstallFailure.ResignFailed =>
                    Format("statusResignFailed", failure.Output ?? failure.Message),
                InstallFailure.TvStoreNotBack =>
                    Format("statusTvStoreNotBack", failure.InstalledVersion is null ? "" : Format("statusTvStoreNotBackVersion", failure.InstalledVersion)),
                InstallFailure.TransportLost =>
                    Text(Constants.LocalizationKeys.ConnectionInterrupted),
                InstallFailure.OperationNotAllowed =>
                    Text(Constants.LocalizationKeys.ApiVersionMismatch),
                InstallFailure.InsufficientSpace =>
                    Failed(Text(Constants.LocalizationKeys.InsufficientSpace)),
                InstallFailure.CertificateInvalidFormat =>
                    Text(Constants.LocalizationKeys.CertificateRejectedInvalidFormat),
                InstallFailure.CertificateNotYetValid =>
                    Text(Constants.LocalizationKeys.CertificateRejectedNotYetValid),
                InstallFailure.CertificateExpired =>
                    Text(Constants.LocalizationKeys.CertificateRejectedExpired),
                InstallFailure.CertificateChainRejected or InstallFailure.CertificateRejected =>
                    Format(Constants.LocalizationKeys.CertificateRejectedOther, failure.Reason ?? "[118, -12]"),
                InstallFailure.CertificateMismatch or InstallFailure.CertificateMismatchAfterRemoval or InstallFailure.CertificateMismatchTvCannotRemove =>
                    Format(Constants.LocalizationKeys.CertificateMismatch, appTitle),
                InstallFailure.PackageIdBlocked =>
                    Format(Constants.LocalizationKeys.PackageIdBlocked, appTitle),
                InstallFailure.InstalledAndTvCannotRemove =>
                    Format(Constants.LocalizationKeys.AlreadyInstalled, appTitle),
                InstallFailure.InstalledAndRemovalOff =>
                    Text(Constants.LocalizationKeys.DeleteExistingNotAllowed),
                InstallFailure.Generic when !string.IsNullOrWhiteSpace(failure.Output) =>
                    Failed(failure.Output!.Trim()),
                _ => failure.Message,
            };
        }
    }
}
