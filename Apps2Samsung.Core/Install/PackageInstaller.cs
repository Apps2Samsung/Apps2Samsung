using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Certificate;
using Apps2Samsung.Configuration;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Install
{
    /// <summary>The signing profile an install is signed with: the two PKCS#12 files, their password and the profile folder.</summary>
    public sealed record SigningProfile(string AuthorP12, string DistributorP12, string Password, string ProfileDir);

    /// <summary>The stages <see cref="PackageInstaller"/> reports as it reaches them.</summary>
    public enum InstallStage
    {
        /// <summary>Reading the TV, patching and re-signing the package.</summary>
        Sign,
        /// <summary>The TV is being touched: old copy removed, package pushed and installed.</summary>
        Install,
        /// <summary>The installed app is being launched.</summary>
        Open,
    }

    /// <summary>
    /// Installs a downloaded .wgt/.tpk on a TV through an <see cref="ISdbEngine"/>, the same way on
    /// every head that has no UI of its own in the loop (the phone, the CLI, #836): read the TV's
    /// capability report → version gate → (older TVs) push the device profile → apply the package
    /// patchers → check the certificate's validity window → re-sign → install, with the error-code
    /// recovery the heads used to each keep a copy of. Settings come from <see cref="IAppConfig"/>.
    /// </summary>
    public sealed class PackageInstaller
    {
        private readonly ISdbEngine _sdb;
        private readonly IAppConfig _config;
        private readonly IEnumerable<IPackagePatcher> _patchers;

        public PackageInstaller(ISdbEngine sdb, IAppConfig config, IEnumerable<IPackagePatcher> patchers)
        {
            _sdb = sdb;
            _config = config;
            _patchers = patchers;
        }

        /// <param name="progress">Human-readable status lines, in order.</param>
        /// <param name="stage">The stage reached, with the TV's install percentage while it reports one.</param>
        /// <param name="ct">Honoured up to the moment the TV is touched for real (old copy removed, package
        /// pushed); after that the install runs to the end, so it never leaves the TV halfway.</param>
        /// <param name="confirmRemove">Asked before the failure recovery removes the installed copy, which
        /// erases that app's settings, sign-in and local data on the TV (#825). False stops the install
        /// with <see cref="RemovalDeclinedException"/>; null (no UI) keeps the remove-and-retry as it was.</param>
        /// <returns>The TV's install output.</returns>
        /// <param name="packageId">Which id to install under (#839): a new one on request, or the one
        /// remembered for this TV. Null installs under the package's own id.</param>
        public async Task<string> InstallAsync(string tvIp, string wgtPath, SigningProfile cert, Action<string>? progress = null,
            Action<InstallStage, int?>? stage = null, CancellationToken ct = default, Func<Task<bool>>? confirmRemove = null,
            PackageIdPolicy? packageId = null)
        {
            stage?.Invoke(InstallStage.Sign, null);
            progress?.Invoke("Reading TV capabilities…");
            var cap = await _sdb.CapabilityAsync(tvIp);
            ct.ThrowIfCancellationRequested();
            var caps = TizenCapabilities.Parse(cap.Output);
            var sdkToolPath = caps.SdkToolPath;
            var version = caps.Version;

            // Another id for this TV, by request or as remembered (#839). Before anything reads the
            // ids: from here on the package is the renamed one.
            await PackageIds.ApplyAsync(wgtPath, packageId, _config, progress);

            // The Tizen app/package ids live in the package's config.xml; needed to remove an old
            // version before install and/or launch the app afterwards.
            var (appId, packageIdInFile) = await WgtManifest.ReadIdsAsync(wgtPath);

            // Pre-install Tizen version gate: a package that declares a required_version newer than
            // this TV fails up front with a clear message instead of signing + pushing and getting
            // back the ambiguous [118, -4] "operation not allowed".
            var requiredVersion = await WgtManifest.ReadRequiredVersionAsync(wgtPath);
            if (WgtManifest.RequiresNewerTizen(version, requiredVersion))
                throw new InvalidOperationException(
                    $"This app needs Tizen {requiredVersion} or newer, but this TV is Tizen {caps.PlatformVersion}. " +
                    "Install an older build of the app, or update the TV's firmware.");

            // Was this package already on the TV before we started? The failure cleanup below only
            // clears a partial from a FRESH install and never removes a pre-existing working app.
            bool wasInstalledBefore = false;
            if (!string.IsNullOrWhiteSpace(packageIdInFile))
            {
                try
                {
                    var listed = await _sdb.AppsAsync(tvIp);
                    wasInstalledBefore = listed?.Output?.Contains(packageIdInFile!, StringComparison.OrdinalIgnoreCase) ?? false;
                }
                catch { /* best-effort; if we can't tell, treat as fresh (cleanup guard still needs packageIdInFile) */ }
            }
            ct.ThrowIfCancellationRequested();

            // Older TVs (<= 4.0) need the distributor device profile pushed before install; newer TVs
            // carry the authorization in the re-signed package itself.
            if (TizenPermitInstall.IsRequired(version))
            {
                progress?.Invoke("Authorizing device…");
                var profileXml = Path.Combine(cert.ProfileDir, "device-profile.xml");
                await TizenPermitInstall.EnsureAsync(_sdb, tvIp, version, sdkToolPath, profileXml);
                ct.ThrowIfCancellationRequested();
            }

            // Per-app modifications before signing: the user's TVApp channels into a TVApp package,
            // then the registered patchers. All of it edits one workspace, so the package is unpacked
            // once and rezipped once however much applies to it.
            var channels = TvAppChannelInjector.AppliesTo(wgtPath)
                ? TvAppChannelInjector.ParseChannelsJson(_config.TvAppChannelsJson)
                : Array.Empty<TvChannel>();
            var patchers = _patchers.Where(p => p.CanHandle(wgtPath)).ToList();

            if (channels.Count > 0 || patchers.Count > 0)
            {
                progress?.Invoke(channels.Count > 0 ? "Applying TVApp channels…" : "Applying customizations…");

                using var workspace = PackageWorkspace.Extract(wgtPath, tvIp);

                if (channels.Count > 0)
                    await TvAppChannelInjector.InjectChannelsAsync(workspace, channels);

                // A patcher that cannot continue throws (UserSettingsPatchException); a soft failure
                // is logged by the patcher and the install goes on, as it always has on both heads.
                foreach (var patcher in patchers)
                    await patcher.ApplyAsync(workspace);

                // A patcher that matched on the file name but found nothing to do leaves the package
                // exactly as downloaded rather than as a recompressed copy of itself.
                workspace.RepackIfChanged();
            }

            // The certificate must already be inside its validity window: Tizen checks the signature
            // against the TV's clock and refuses one whose start date is still in the future
            // ("Certificate in signature is not valid yet"). Only waiting fixes that, so stop before
            // signing and pushing; the caller turns this into a countdown.
            var validity = CertificateValidity.CheckSigningProfile(cert.AuthorP12, cert.DistributorP12, cert.Password);
            if (validity.IsNotYetValid)
                throw new CertificateNotYetValidException(validity);

            progress?.Invoke("Re-signing package…");
            var resign = await _sdb.ResignAsync(wgtPath, cert.AuthorP12, cert.DistributorP12, cert.Password);
            if (resign.ExitCode != 0 || resign.Output.Contains("Re-sign failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Re-sign failed: {Detail(resign.Error, resign.Output)}");

            // Last point to stop: from here the TV loses its old copy and gets the new one.
            ct.ThrowIfCancellationRequested();
            stage?.Invoke(InstallStage.Install, null);

            if (_config.DeletePreviousInstall && !string.IsNullOrWhiteSpace(packageIdInFile))
            {
                progress?.Invoke("Removing old version…");
                try { await _sdb.UninstallAsync(tvIp, packageIdInFile!); } catch { /* nothing to remove */ }
            }

            progress?.Invoke("Installing on TV…");
            var install = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath, InstallPercentFor(progress, stage));
            // The Tizen install helper returns a zero exit code (cmd_ret:0) even when the app install
            // itself failed (e.g. "app_id[...] install failed[118]"), so classify the OUTPUT as well
            // and route real failures into recovery.
            if (install.ExitCode != 0 || TizenInstallDiagnostics.IndicatesFailure(install.Output))
                install = await RecoverInstallAsync(tvIp, wgtPath, sdkToolPath, packageIdInFile, install, progress, wasInstalledBefore, confirmRemove);

            if (_config.OpenAfterInstall && !string.IsNullOrWhiteSpace(appId))
            {
                stage?.Invoke(InstallStage.Open, null);
                progress?.Invoke("Launching on TV…");
                try { await _sdb.LaunchAsync(tvIp, appId!); } catch { /* launch is best-effort */ }
            }

            return install.Output;
        }

        // The TV reports its install percentage line by line (installing[9] … installing[100]).
        // Synchronous (SyncProgress) so the values keep the TV's order.
        private static IProgress<int>? InstallPercentFor(Action<string>? progress, Action<InstallStage, int?>? stage) =>
            progress is null && stage is null ? null : new SyncProgress<int>(percent =>
            {
                stage?.Invoke(InstallStage.Install, percent);
                progress?.Invoke($"Installing on TV… {percent}%");
            });

        // [118, -22] from the TV's security manager. Worded the same as the desktop head's
        // "packageIdBlocked" string, which is the translated one.
        public const string PackageIdBlockedMessage =
            "The TV refused this package because the package id it uses is blocked on this set ([118, -22]). " +
            "An older copy is still registered under a different signing certificate, or an interrupted " +
            "uninstall left a record behind that the app list no longer shows. Delete the app on the TV itself " +
            "(Apps list, press and hold, Remove) and install again, install a build that ships its own package " +
            "id (a \"secondary\" variant), or install it under a new package id (the option on the app's sheet, " +
            "--new-id in the CLI): the app then gets a fresh id on this TV, which Apps2Samsung remembers for updates.";

        // Interprets a non-zero install result and, where it helps, removes the old copy and retries once.
        // Returns the successful result or throws with an actionable message.
        private async Task<ProcessResult> RecoverInstallAsync(
            string tvIp, string wgtPath, string sdkToolPath, string? packageId, ProcessResult failed, Action<string>? progress,
            bool wasInstalledBefore, Func<Task<bool>>? confirmRemove)
        {
            var output = failed.Output ?? string.Empty;

            // Fresh-install cleanup guard: a failed install of an app that was NOT already on the TV can
            // leave a partial package dir wasting storage. Best-effort clear it before we throw, but ONLY
            // when it was a fresh install (never remove a pre-existing working app on a failed reinstall).
            async Task ClearPartialIfFresh()
            {
                if (!wasInstalledBefore && !string.IsNullOrWhiteSpace(packageId))
                {
                    try { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* best-effort */ }
                }
            }

            // Environmental: a broken route to the TV. Retrying the same push can't help.
            if (TizenInstallDiagnostics.IsTransportLost(output))
                throw new InvalidOperationException(
                    "Connection to the TV was interrupted. Check Wi-Fi (and that the TV is awake), then try again.");

            // [118, -4] "operation not allowed": ambiguous between the app targeting a newer Tizen than
            // this TV and the package needing Partner signing / a privilege the certificate doesn't grant.
            if (TizenInstallDiagnostics.IsApiVersionMismatch(output))
                throw new InvalidOperationException(
                    "The TV refused this package ([118, -4]). Either the app needs a newer Tizen than this TV, or it needs Partner signing / a privilege your certificate doesn't allow. If other apps install fine, turn on Partner signing and retry, or try an older build.");

            // [116] "download failed": the TV has no room for the package (#666).
            if (TizenInstallDiagnostics.IsInsufficientSpace(output))
            {
                await ClearPartialIfFresh();
                throw new InvalidOperationException(
                    "Not enough free space on the TV [116]. Remove some apps on the TV (Settings → Support → Device Care → Manage Storage), then install again.");
            }

            // The TV refused the certificate itself (#907): removing an installed copy can't help, and
            // there usually is none, so no prompt and no retry. Say what the TV said and what fixes it.
            var rejection = TizenInstallDiagnostics.GetCertificateRejection(output);
            if (rejection != CertificateRejection.None)
            {
                await ClearPartialIfFresh();
                throw new InvalidOperationException(CertificateRejectionMessage(rejection, output));
            }

            bool certMismatch = TizenInstallDiagnostics.IsCertificateMismatch(output);

            // [118, -22]: the TV's security manager refused the package id itself. Removing the old copy
            // is the one remedy we can drive from here (#702).
            bool idBlocked = TizenInstallDiagnostics.IsPackageIdBlocked(output);

            // Recoverable by removing the old copy first: try exactly one clean reinstall.
            bool recoverable = certMismatch ||
                               idBlocked ||
                               TizenInstallDiagnostics.IsPackageIdConflict(output) ||
                               TizenInstallDiagnostics.IsGenericFailure(output);

            if (recoverable && _config.TryOverwrite && !string.IsNullOrWhiteSpace(packageId))
            {
                // Removing the old copy erases its settings, sign-in and local data, so the user decides
                // first (#825), unless "Uninstall installed version first" already made that choice.
                if (!_config.DeletePreviousInstall && confirmRemove is not null && !await confirmRemove())
                    throw new RemovalDeclinedException();

                progress?.Invoke("Install failed — removing the old copy and retrying…");
                try { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* best-effort */ }

                var retry = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath, InstallPercentFor(progress, null));
                if (retry.ExitCode == 0 && !TizenInstallDiagnostics.IndicatesFailure(retry.Output))
                    return retry;

                // Still failing after a clean slate: surface the most useful message.
                if (TizenInstallDiagnostics.IsCertificateMismatch(retry.Output))
                    throw new InvalidOperationException(
                        "The TV already has this app signed with a different certificate. Remove it on the TV (Apps → delete), then install again.");

                if (TizenInstallDiagnostics.IsPackageIdBlocked(retry.Output))
                    throw new InvalidOperationException(PackageIdBlockedMessage);

                await ClearPartialIfFresh();
                throw new InvalidOperationException($"Install failed: {Detail(retry.Error, retry.Output)}");
            }

            // Not retried (recovery off or no package id): the clearest message we can give.
            if (idBlocked)
                throw new InvalidOperationException(PackageIdBlockedMessage);

            if (certMismatch)
                throw new InvalidOperationException(
                    "The TV already has this app signed with a different certificate. Remove it on the TV (Apps → delete) and install again, or enable \"Override existing app\".");

            await ClearPartialIfFresh();
            throw new InvalidOperationException($"Install failed: {Detail(failed.Error, output)}");
        }

        private static string Detail(string error, string output) =>
            !string.IsNullOrWhiteSpace(error) ? error : output.Trim();

        // English, like the other Core messages: the mobile head has no string catalog. The desktop
        // head has the same four texts as localized keys (certificateRejected*).
        private static string CertificateRejectionMessage(CertificateRejection rejection, string output) => rejection switch
        {
            CertificateRejection.InvalidFormat =>
                "The TV rejected the signing certificate itself ([118, -12], \"Invalid format of certificate in signature\"). This is not about an installed copy. Most often the TV's date and time are off, which happens on a TV without internet or an antenna signal: check Settings → General → System Manager → Time on the TV, then install again. If the clock is right, create a new certificate and retry.",
            CertificateRejection.NotYetValid =>
                "The TV says the signing certificate isn't valid yet ([118, -12]). The certificate is fine on this device, so the TV's clock is behind: set the TV's date and time (Settings → General → System Manager → Time) or wait a few minutes, then install again.",
            CertificateRejection.Expired =>
                "The TV says the signing certificate has expired ([118, -12]). Either the TV's clock is ahead (Settings → General → System Manager → Time) or the certificate is past its end date: create a new one and install again.",
            _ =>
                $"The TV rejected the signing certificate ([118, -12]: {TizenInstallDiagnostics.CertificateRejectionReason(output) ?? "check certificate error"}). Removing an installed copy won't help. Check the TV's date and time (Settings → General → System Manager → Time); if that is right, create a new certificate and install again.",
        };
    }

    /// <summary>
    /// The user kept the installed copy when the install could only continue by removing it (#825).
    /// Not a failure of the TV or the package: the TV is exactly as it was.
    /// </summary>
    public sealed class RemovalDeclinedException : Exception
    {
        public RemovalDeclinedException() : base("The installed copy was kept at the user's request.") { }
    }
}
