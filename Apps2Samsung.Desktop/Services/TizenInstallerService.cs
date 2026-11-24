using Apps2Samsung.Certificate;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Localization;
using Apps2Samsung.Sdb;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class TizenInstallerService : ITizenInstallerService, ITvNameResolver
    {
        private readonly HttpClient _httpClient;
        private readonly IDialogService _dialogService;
        private readonly AppSettings _appSettings;
        private readonly IEnumerable<IPackagePatcher> _packagePatchers;
        private readonly ISdbEngine _sdb;
        private readonly ISamsungLoginService _login;
        private readonly CertificateProvisioningService _provisioning;
        private readonly Apps2Samsung.Install.PackageInstaller _installer;
        // The certificate authorities in use: the shipped ones, or a newer set the user agreed to download (#1153).
        private readonly AuthorityStore _authorities;

        public string? PackageCertificate { get; set; }

        public TizenInstallerService(
            HttpClient httpClient,
            IDialogService dialogService,
            AppSettings appSettings,
            IEnumerable<IPackagePatcher> packagePatchers,
            JellyfinApiClient jellyfinApiClient,
            ISdbEngine sdb,
            ISamsungLoginService samsungLogin,
            CertificateProvisioningService provisioning)
        {
            _httpClient = httpClient;
            _dialogService = dialogService;
            _appSettings = appSettings;
            _packagePatchers = packagePatchers;
            _sdb = sdb;
            _login = samsungLogin;
            _provisioning = provisioning;
            _installer = new Apps2Samsung.Install.PackageInstaller(sdb, appSettings, packagePatchers);
            _authorities = new AuthorityStore(
                Path.Combine(AppSettings.DataFolderPath, "Authorities"),
                Path.Combine(AppSettings.ProfilePath, "ca"));
        }

        #region Package Download

        // Downloads a .wgt and verifies it is a valid archive before handing it back.
        public async Task<string> DownloadPackageAsync(string downloadUrl)
        {
            var fileName = UrlHelper.GetFileNameFromUrl(downloadUrl);
            var localPath = Path.Combine(AppSettings.DownloadPath, fileName);

            // Reuse a cached copy, but only if it's actually a valid archive AND still byte-for-byte
            // the file we downloaded. A previous download that was interrupted (network drop, VPN
            // reset, app quit) can leave a truncated file here, and returning it blindly makes the
            // patcher fail later with "End of Central Directory record could not be found". Versions
            // before the install pipeline staged its own copy also patched and re-signed this file in
            // place, so a cache written by one of those is an already-patched package and patching it
            // again stacks the injections (#702). Either way: drop it and download a clean one.
            if (File.Exists(localPath))
            {
                if (IsValidZipArchive(localPath) && MatchesCacheStamp(localPath))
                    return localPath;

                Trace.WriteLine($"[Download] Cached package is corrupt or no longer the file that was downloaded, re-downloading: {localPath}");
                TryDelete(localPath);
            }

            Directory.CreateDirectory(AppSettings.DownloadPath);

            // Download to a temp file and only promote it to the final path once the transfer
            // completes and validates, so an interrupted download never poisons the cache.
            var tempPath = localPath + ".part";
            TryDelete(tempPath);

            try
            {
                using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using (var contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream);
                }

                // If the server advertised a size, make sure we got all of it.
                var expected = response.Content.Headers.ContentLength;
                var actual = new FileInfo(tempPath).Length;
                if (expected.HasValue && actual != expected.Value)
                    throw new IOException($"Download incomplete: received {actual} of {expected.Value} bytes.");

                // .wgt is a zip, if the trailer isn't there, the file is truncated/corrupt.
                if (!IsValidZipArchive(tempPath))
                    throw new InvalidDataException("Downloaded package is not a valid .wgt archive (corrupt or incomplete).");

                File.Move(tempPath, localPath, overwrite: true);
                WriteCacheStamp(localPath, downloadUrl);
                return localPath;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        // A .wgt is a zip archive. Opening it and reading the entry table forces the zip
        // reader to locate the End-of-Central-Directory record, so a truncated/empty file
        // fails here instead of deep inside the patcher.
        private static bool IsValidZipArchive(string path)
        {
            try
            {
                // 22 bytes is the minimum size of an empty zip (the EOCD record alone),
                // so anything smaller can't be a real package.
                if (new FileInfo(path).Length < 22)
                    return false;

                using var archive = System.IO.Compression.ZipFile.OpenRead(path);
                return archive.Entries.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        // Where the note about a cached download lives: a sibling file under a dot-directory, so the
        // download folder still shows nothing but .wgt files.
        private static string CacheStampPath(string packagePath) => Path.Combine(
            Path.GetDirectoryName(packagePath)!, ".stamps", Path.GetFileName(packagePath) + ".json");

        // Records what was downloaded, so a later run can tell a pristine download from a file that
        // has been modified since (an older build patching the cache in place, or a user editing it).
        private static void WriteCacheStamp(string packagePath, string downloadUrl)
        {
            try
            {
                var stamp = CacheStampPath(packagePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
                var info = new FileInfo(packagePath);
                File.WriteAllText(stamp, JsonSerializer.Serialize(new CacheStamp
                {
                    Url = downloadUrl,
                    Length = info.Length,
                    WrittenUtc = info.LastWriteTimeUtc,
                }));
            }
            catch (Exception ex)
            {
                // A cache note is an optimisation; failing to write it only costs a re-download.
                Trace.WriteLine($"[Download] Could not record the cache stamp for {packagePath}: {ex.Message}");
            }
        }

        private static bool MatchesCacheStamp(string packagePath)
        {
            try
            {
                var stamp = CacheStampPath(packagePath);
                if (!File.Exists(stamp))
                    return false; // written by a build that patched the cache in place, so assume it did

                var recorded = JsonSerializer.Deserialize<CacheStamp>(File.ReadAllText(stamp));
                if (recorded is null)
                    return false;

                var info = new FileInfo(packagePath);
                return recorded.Length == info.Length && recorded.WrittenUtc == info.LastWriteTimeUtc;
            }
            catch
            {
                return false;
            }
        }

        private sealed class CacheStamp
        {
            public string Url { get; set; } = "";
            public long Length { get; set; }
            public DateTime WrittenUtc { get; set; }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Download] Could not delete '{path}': {ex.Message}");
            }
        }

        #endregion

        #region Main Installation Flow

        public async Task<InstallResult> InstallPackageAsync(
            string packageUrl,
            string tvIpAddress,
            CancellationToken cancellationToken,
            ProgressCallback? progress = null,
            Action? onSamsungLoginStarted = null,
            bool? wasAlreadyInstalled = null,
            IProgress<int>? installProgress = null,
            string? catalogKey = null)
        {
            // wasAlreadyInstalled is no longer needed: PackageInstaller reads the TV's app list itself
            // before it decides what a failed install may clean up.
            var run = new InstallRun
            {
                ForceLogin = _appSettings.ForceSamsungLogin,
                // "Uninstall installed version first" is already the user's choice to remove, so the
                // recovery does not ask again; otherwise the question is open until it comes up.
                RemovalConfirmed = _appSettings.DeletePreviousInstall ? true : null,
                SourcePackage = packageUrl,
                InstallProgress = installProgress,
                CatalogKey = catalogKey,
            };

            try
            {
                var result = await RunInstallAsync(tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, run);
                // Renamed because another app on the TV has this id (#1028): the result says so.
                if (result.Success && run.Rename?.CollidedWith is { } other)
                {
                    var note = string.Format("installedAsSeparateApp".Localized(), run.Rename.NewId, other);
                    progress?.Invoke(note);
                    return new InstallResult { Success = true, Note = note };
                }
                return result;
            }
            finally
            {
                run.Dispose();
            }
        }

        // One install attempt: the desktop's certificate step, then Core's PackageInstaller, the
        // same install the phone and the CLI run (#1145). What is desktop about it are the sheets and
        // dialogs, handed in as callbacks, and the outcomes that open one (a Tizen certificate the TV
        // refuses, a certificate that is not valid yet), which run the attempt once more.
        private async Task<InstallResult> RunInstallAsync(
            string tvIpAddress,
            CancellationToken cancellationToken,
            ProgressCallback? progress,
            Action? onSamsungLoginStarted,
            InstallRun run)
        {
            // The installer renames, patches and signs the package in place. None of that may touch
            // the file the caller owns, so every attempt works on a throwaway copy and
            // run.SourcePackage stays pristine (#702).
            var packagePath = run.StageWorkingCopy();

            try
            {
                progress?.Invoke(Constants.LocalizationKeys.ConnectingToDevice.Localized());

                var deviceInfo = await GetDeviceInfoAsync(tvIpAddress);
                if (deviceInfo == null)
                {
                    progress?.Invoke(Constants.LocalizationKeys.TvNameNotFound.Localized());
                    return InstallResult.FailureResult(Constants.LocalizationKeys.TvNameNotFound.Localized());
                }
                run.TvDuid = deviceInfo.Duid;
                run.TvName = deviceInfo.Name;

                var certificateResult = await HandleCertificateAsync(
                    tvIpAddress,
                    deviceInfo,
                    packagePath,
                    progress,
                    cancellationToken,
                    onSamsungLoginStarted,
                    run);

                if (!certificateResult.Success)
                    return certificateResult.InstallResult;

                var profile = new Apps2Samsung.Install.SigningProfile(
                    certificateResult.AuthorP12,
                    certificateResult.DistributorP12,
                    certificateResult.P12Password,
                    Path.GetDirectoryName(certificateResult.AuthorP12)!);

                try
                {
                    await _installer.InstallAsync(
                        tvIpAddress,
                        packagePath,
                        profile,
                        progress: line => progress?.Invoke(ProgressText.Localize(line, key => key.Localized())),
                        stage: (stage, percent) =>
                        {
                            if (stage == Apps2Samsung.Install.InstallStage.Install && percent is { } value)
                                run.InstallProgress?.Report(value);
                        },
                        ct: cancellationToken,
                        confirmRemove: () => ConfirmRemovalAsync(run, packagePath),
                        packageId: new Apps2Samsung.Install.PackageIdPolicy(
                            deviceInfo.Duid,
                            AppSettings.Default.InstallUnderNewPackageId,
                            AppSettings.Default.InstallUnderOwnPackageId,
                            run.CatalogKey),
                        confirmReplace: collision => ConfirmReplaceAsync(run, collision),
                        renamed: rename =>
                        {
                            // A later attempt re-applies the remembered rename, which no longer says why; keep the first.
                            if (run.Rename is null || rename.CollidedWith is not null)
                                run.Rename = rename;
                        },
                        alternativeCertificate: _ => OfferOlderCertificateAsync(run, packagePath),
                        keepInstalledWhenOverwriteOff: true);
                }
                catch (CertificateNotYetValidException notYet) when (notYet.Result.ValidFromLocal is { } validFrom)
                {
                    // Tizen checks the signature against the TV's clock and refuses a certificate whose
                    // start date is still ahead; only waiting helps. Count down, then run again.
                    Trace.WriteLine($"[Cert] Certificate valid from {validFrom.ToString("f", CultureInfo.InvariantCulture)}; holding the install.");
                    progress?.Invoke(Constants.LocalizationKeys.CertificateWaiting.Localized());
                    if (!await _dialogService.ShowCertificateCountdownAsync(validFrom))
                    {
                        // Cancelled during the wait, a deliberate stop, not a failed install.
                        progress?.Invoke(Constants.LocalizationKeys.CertificateWaitCancelled.Localized());
                        return InstallResult.CancelledResult(Constants.LocalizationKeys.CertificateWaitCancelled.Localized());
                    }

                    // The certificate that was just waited for is the one to use: no fresh sign-in.
                    run.ForceLogin = false;
                    return await RunInstallAsync(tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, run);
                }
                catch (Apps2Samsung.Install.CertificateChainRejectedException refusedChain) when (run.TizenProfile is { } tizenProfile)
                {
                    // The TV refused the Tizen certificate's chain (#988): it only takes a Samsung one.
                    // The sheet says so and offers the switch; a yes runs this install again, Samsung-signed.
                    progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());
                    Trace.WriteLine($"[Install] Tizen certificate refused by {tvIpAddress}: {refusedChain.Output ?? refusedChain.Message}");
                    var reason = refusedChain.Reason;
                    var switchToSamsung = await _dialogService.ShowTizenCertificateRefusedAsync(
                        run.TvName ?? tvIpAddress, tizenProfile.Level, tizenProfile.ProfileName, reason,
                        CertificateProvisioningService.AuthorValidUntil(tizenProfile.ProfileDir));
                    if (switchToSamsung)
                    {
                        _appSettings.UseSamsungCertificate();
                        run.TizenProfile = null;
                        return await RunInstallAsync(tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, run);
                    }
                    var refused = string.Format("statusTizenCertificateRefused".Localized(), run.TvName ?? tvIpAddress);
                    progress?.Invoke(refused);
                    return InstallResult.ShownFailureResult(refused);
                }
                catch (Apps2Samsung.Install.RemovalDeclinedException)
                {
                    return RemovalDeclined(packagePath, progress);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Stopped by the user before the TV was touched: not a failed install.
                    var cancelled = "statusInstallCancelled".Localized();
                    progress?.Invoke(cancelled);
                    return InstallResult.CancelledResult(cancelled);
                }
                catch (Apps2Samsung.Install.ReplaceDeclinedException declined)
                {
                    var message = string.Format("replaceDeclined".Localized(), declined.Collision.OccupantName, declined.Collision.PackageName);
                    progress?.Invoke(message);
                    return InstallResult.FailureResult(message);
                }
                catch (Apps2Samsung.Install.InstallFailedException failed)
                {
                    // Core says which situation it is; the text is the desktop's translated one.
                    progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());
                    Trace.WriteLine($"[Install] {failed.Failure} on {tvIpAddress}: {failed.Output ?? failed.Message}");
                    var message = Apps2Samsung.Install.InstallFailureText.Describe(failed, GetPackageAppTitle(packagePath), key => key.Localized());
                    progress?.Invoke(message);
                    return InstallResult.FailureResult(message);
                }

                progress?.Invoke(Constants.LocalizationKeys.InstallationSuccessful.Localized());
                return InstallResult.SuccessResult();
            }
            catch (Apps2Samsung.Packaging.UserSettingsPatchException ex)
            {
                // The package changed upstream: the text a user setting replaces is gone (#791).
                Trace.WriteLine($"[UserSettings] {ex.Message}");
                var message = ex.Find is null
                    ? string.Format("statusUserSettingsFileMissing".Localized(), ex.PackageFile, ex.File)
                    : string.Format("statusUserSettingsFindMissing".Localized(), ex.PackageFile, ex.Find, ex.File);
                progress?.Invoke(message);
                return InstallResult.FailureResult(message);
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Installation error: {ex}");

                // Surface the innermost cause too, outer messages like "The SSL connection could
                // not be established, see inner exception" are useless on their own (e.g. an old
                // Windows 7 TLS stack rejecting GitHub's certificate chain).
                var baseEx = ex.GetBaseException();
                var detail = baseEx.Message != ex.Message
                    ? $"{ex.Message} ({baseEx.Message})"
                    : ex.Message;
                return InstallResult.FailureResult(detail);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tvIpAddress))
                    await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        #endregion

        #region Device Preparation

        private async Task<DeviceInfo?> GetDeviceInfoAsync(string tvIpAddress)
        {
            string tvName = await GetTvNameAsync(tvIpAddress);
            if (string.IsNullOrEmpty(tvName))
                return null;

            string tvDuid = await GetTvDuidAsync(tvIpAddress);
            // Reject a malformed DUID (e.g. an SDB transport-error string) so it never ends up baked
            // into the certificate's device-id SAN. A Tizen certificate (#988) names no TV, so a TV
            // that will not hand its DUID out only loses the package-id rename memory (#839), not
            // the install.
            if (!TizenDuid.IsValid(tvDuid))
            {
                Trace.WriteLine($"[Cert] Invalid TV DUID read from {tvIpAddress}: '{tvDuid}'");
                if (!Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind))
                    return null;
                tvDuid = string.Empty;
            }

            var (tizenOs, sdkToolPath) = await FetchCapabilitiesAsync(tvIpAddress);

            if (string.IsNullOrEmpty(tizenOs))
                tizenOs = Constants.Defaults.TizenOsVersion;

            return new DeviceInfo
            {
                Name = tvName,
                Duid = tvDuid,
                TizenVersion = new Version(tizenOs),
                SdkToolPath = sdkToolPath
            };
        }

        #endregion

        #region Certificate Handling

        private async Task<CertificateResult> HandleCertificateAsync(
            string tvIpAddress,
            DeviceInfo deviceInfo,
            string packagePath,
            ProgressCallback? progress,
            CancellationToken cancellationToken,
            Action? onSamsungLoginStarted,
            InstallRun run)
        {
            // The stock Jellyfin build on a TV between the two thresholds: it is built with the shipped
            // certificate, and that is what it is signed with here, whatever Settings says and whatever
            // this install changes in it (a new package id, an icon), so the TV keeps seeing the
            // author it knows. Every install signs, as on the phone and the CLI (#1148, #1149).
            if (Apps2Samsung.Certificate.CertificatePreference.StockBuildKeepsShippedCertificate(
                    deviceInfo.TizenVersion,
                    Path.GetFileName(packagePath),
                    serverConfigured: !string.IsNullOrEmpty(_appSettings.JellyfinIP),
                    forceLogin: run.ForceLogin)
                && !run.SkipShippedCertificate)
            {
                var shipped = Path.Combine(AppSettings.BundledCertificatePath, Constants.AppIdentifiers.JellyfinAppName);
                if (await CheckShippedCertificateAsync(deviceInfo, run) is { } stopped)
                    return stopped;
                // "Sign in with Samsung instead": this attempt goes on below, with the certificate from Settings.
                if (run.SkipShippedCertificate)
                    return await HandleCertificateAsync(tvIpAddress, deviceInfo, packagePath, progress, cancellationToken, onSamsungLoginStarted, run);
                Trace.WriteLine("[Cert] Stock Jellyfin build: signing with the shipped certificate.");
                // Not a profile in the store: there is no older copy of it in the backups to offer.
                PackageCertificate = null;
                return new CertificateResult
                {
                    Success = true,
                    AuthorP12 = Path.Combine(shipped, Constants.Certificate.AuthorFileName),
                    DistributorP12 = Path.Combine(shipped, Constants.Certificate.DistributorFileName),
                    P12Password = (await File.ReadAllTextAsync(Path.Combine(shipped, Constants.Certificate.PasswordFileName))).Trim(),
                };
            }

            string selectedCertificate = _appSettings.Certificate;
            var chosenCertificate = _appSettings.ChosenCertificates;

            // Public and Partner distributor certs are kept as separate profiles ("Jelly2Sams - Public"
            // / "Jelly2Sams - Partner") so both can coexist and each is reused independently, switching
            // level never clobbers the other.
            // The package requires Partner when the selected package's manifest declares it, OR the
            // package itself declares a partner-level privilege (e.g. vpnservice in a .wgt's
            // config.xml, drminfo in a .tpk's tizen-manifest.xml) or a Partner-only launch setting
            // (on-boot="true" / auto-restart="true"), the automatic binding: a package that needs a
            // restricted API or launch mode must declare it, so we don't track cert levels per package.
            var partnerPrivilege = Apps2Samsung.Packaging.WgtPrivileges.FindPartnerPrivilege(packagePath);
            bool requirePartner = _appSettings.RequiresPartnerSigning || partnerPrivilege is not null;

            // Intermediate Tizen versions take the shipped certificate, no Samsung sign-in. It is
            // Public only, so a Partner choice or a package that needs Partner skips it (#1107).
            bool shippedApplies = selectedCertificate == Constants.AppIdentifiers.Jelly2SamsDefault &&
                !run.SkipShippedCertificate &&
                !Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind) &&
                Apps2Samsung.Certificate.CertificatePreference.ShippedCertificateApplies(
                    deviceInfo.TizenVersion, _appSettings.CertificatePreference, requirePartner);
            if (shippedApplies)
            {
                // The shipped certificate has an end date of its own (#1153); "Sign in with Samsung
                // instead" leaves it out for this attempt.
                if (await CheckShippedCertificateAsync(deviceInfo, run) is { } stopped)
                    return stopped;
                shippedApplies = !run.SkipShippedCertificate;
            }
            if (shippedApplies)
            {
                selectedCertificate = Constants.AppIdentifiers.JellyfinAppName;
                chosenCertificate = new ExistingCertificates
                {
                    Name = Constants.AppIdentifiers.JellyfinAppName,
                    Duid = deviceInfo.Duid,
                    File = Path.Combine(AppSettings.BundledCertificatePath, Constants.AppIdentifiers.JellyfinAppName, Constants.Certificate.AuthorFileName)
                };
            }

            string authorp12, distributorp12, p12Password;

            // A Tizen certificate (#988): the SDK's, made here, no account and no DUID. Its sheet's
            // way out is a Samsung certificate: the setting is switched and the Samsung path below
            // runs as if that had been the choice all along.
            // The authority a Tizen certificate is signed with has an end date (#1153). "Sign in with
            // Samsung instead" switches the setting, so the Samsung path below runs.
            if (Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind)
                && await CheckAuthoritiesAsync(AuthoritySet.Tizen, deviceInfo, cancellationToken) is { } tizenStopped)
                return tizenStopped;

            if (Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind))
            {
                var tizen = await HandleTizenCertificateAsync(tvIpAddress, deviceInfo, requirePartner, progress, cancellationToken, run);
                if (tizen is not null)
                    return tizen;
            }

            // The package can only be installed Partner-signed, and there's no Partner certificate yet:
            // switch the Settings choice to Partner so one is actually created (and so the UI matches
            // what we sign with) instead of letting the install fail on the TV with
            // MISMATCHED_PRIVILEGE_LEVEL. Same rule as the phone.
            if (Apps2Samsung.Certificate.CertificatePreference.ShouldSwitchToPartner(
                    _appSettings.CertificatePreference, requirePartner,
                    CertificateProvisioningService.HasProfile(AppSettings.CertificatePath, CertificatePrivilegeLevel.Partner)))
            {
                Trace.WriteLine($"[Cert] Switching the certificate to Partner: package declares '{partnerPrivilege ?? "cert_level partner"}'.");
                _appSettings.EnablePartnerSigning();
                progress?.Invoke(Constants.LocalizationKeys.PartnerSigningAutoEnabled.Localized());
            }

            // Automatic / Public / Partner from Settings, a hard Partner requirement winning (Core).
            var requestedLevel = Apps2Samsung.Certificate.CertificatePreference.ResolveLevel(
                _appSettings.CertificatePreference, requirePartner);
            var jelly2SamsDir = Path.Combine(AppSettings.CertificatePath, AutoCertProfileName(requestedLevel));
            bool hasAuthor = HasUsableAuthorCert(jelly2SamsDir);

            // Older Tizen TVs use the shipped "Jellyfin" certificate (set just above), it's always
            // present and never regenerated, so it bypasses all Samsung-login / regeneration logic
            // and is just reused from disk (unless the user forces a fresh login).
            bool isBundledJellyfin = selectedCertificate == Constants.AppIdentifiers.JellyfinAppName;

            // A Samsung certificate chains to Samsung's authorities, which end one day as well (#1153).
            if (!isBundledJellyfin && await CheckAuthoritiesAsync(AuthoritySet.Samsung, deviceInfo, cancellationToken) is { } samsungStopped)
                return samsungStopped;

            // "Auto" mode = the app drives cert selection itself: no real pick, the "(default)"
            // placeholder, or one of our generated "Jelly2Sams[ - Public/Partner]" profiles. In auto
            // mode we REUSE an existing valid profile for the requested level rather than logging in
            // again. Crucially, the "(default)"/empty placeholder must NOT force a fresh Samsung login
            // when a usable level cert is already on disk, that placeholder was the cause of the
            // "sign in every time" bug (the dropdown often reverts to "(default)" even though
            // "Jelly2Sams - Public/Partner" exists).
            bool autoMode = string.IsNullOrEmpty(selectedCertificate) || IsAutoCertName(selectedCertificate);

            // Auto profile (no real pick / "(default)" / a generated "Jelly2Sams - Public/Partner"):
            // delegate the whole reuse/regenerate/mint decision to the shared Core
            // CertificateProvisioningService, so desktop and mobile use one source of truth for cert
            // reuse (it reuses a valid profile covering this TV with NO Samsung login, regenerates only
            // the distributor when a DUID isn't covered, and mints a full profile otherwise / on force).
            // The bundled "Jellyfin" cert and user-imported certs fall through to the original path
            // below, unchanged.
            if (!isBundledJellyfin && autoMode)
            {
                var caPath = _authorities.SamsungFolder;
                using var sheet = new CertificateSheetRun(_dialogService, deviceInfo.Name, cancellationToken, deviceInfo.Duid);
                try
                {
                    var profile = await _provisioning.EnsureAsync(
                        deviceDuid: deviceInfo.Duid,
                        level: requestedLevel,
                        storePath: AppSettings.CertificatePath,
                        caPath: caPath,
                        manualDuids: ParseDuids(_appSettings.ManualDuids),
                        forceLogin: run.ForceLogin,
                        onLoginStarting: () =>
                        {
                            progress?.Invoke(Constants.LocalizationKeys.SamsungLogin.Localized());
                            onSamsungLoginStarted?.Invoke();
                        },
                        progress: sheet.Progress(progress),
                        beforeLoginAsync: sheet.OpenAsync,
                        ct: sheet.Token);
                    await sheet.FinishAsync();

                    PackageCertificate = profile.ProfileName;

                    // Permit-install for older Tizen versions (thresholds shared with Core).
                    await Apps2Samsung.Sdb.TizenPermitInstall.EnsureAsync(
                        _sdb, tvIpAddress, deviceInfo.TizenVersion, deviceInfo.SdkToolPath,
                        Path.Combine(Path.GetDirectoryName(profile.AuthorP12)!, Constants.Certificate.DeviceProfileFileName));

                    return new CertificateResult
                    {
                        Success = true,
                        AuthorP12 = profile.AuthorP12,
                        DistributorP12 = profile.DistributorP12,
                        P12Password = profile.Password
                    };
                }
                catch (Exception ex)
                {
                    return await EndCertificateRunAsync(sheet, ex);
                }
            }

            // A full Samsung profile (fresh keypair + new author cert) is only needed when the user
            // forces a login, or there's genuinely no usable author cert for this level yet (first run
            // for the level, or it's missing/expired). Because the folder is level-specific, switching
            // Public<->Partner naturally has no author yet and generates a fresh profile without
            // touching the other level.
            bool needsFullProfile = run.ForceLogin ||
                                    (!isBundledJellyfin && !hasAuthor);

            // DUIDs the user manually pre-authorized + DUIDs already covered by THIS level's distributor
            // cert. One distributor cert can cover several TVs (multiple device-id SAN entries), so we
            // only regenerate when a needed DUID isn't covered yet.
            var manualDuids = ParseDuids(_appSettings.ManualDuids);
            var coveredDuids = (hasAuthor && !isBundledJellyfin)
                ? GetCoveredDuids(jelly2SamsDir)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool duidsCovered = coveredDuids.Contains(deviceInfo.Duid) &&
                                manualDuids.All(coveredDuids.Contains);

            // Generated author cert exists but the distributor cert doesn't yet cover this TV (or a
            // newly-added manual DUID): regenerate ONLY the distributor (reusing the author keypair)
            // so the author identity stays byte-identical and apps on other TVs stay overwritable.
            bool needsDistributorOnly = !needsFullProfile &&
                                        !isBundledJellyfin &&
                                        !duidsCovered;

            if (needsFullProfile || needsDistributorOnly)
            {
                using var sheet = new CertificateSheetRun(_dialogService, deviceInfo.Name, cancellationToken, deviceInfo.Duid);
                try
                {
                    await sheet.OpenAsync(requestedLevel, sheet.Token);
                    progress?.Invoke(Constants.LocalizationKeys.SamsungLogin.Localized());
                    onSamsungLoginStarted?.Invoke();

                    SamsungAuth auth = await _login.LoginAsync(sheet.Token);

                    if (string.IsNullOrEmpty(auth.access_token))
                        throw new InvalidOperationException("statusSamsungAuthFailed".Localized());

                    // The distributor CSR needs the account's email for its subject. Samsung accounts
                    // without one hand us null, which used to surface as a bare "Object reference not set
                    // to an instance of an object" from inside BouncyCastle (issue #606). Point the user
                    // straight at their account page instead.
                    if (string.IsNullOrWhiteSpace(auth.inputEmailID))
                        throw new SamsungAccountEmailMissingException();

                    progress?.Invoke(Constants.LocalizationKeys.CreatingCertificateProfile.Localized());
                    var certificateService = new TizenCertificateService(
                        _httpClient,
                        new CertificateEndpoints(
                            _appSettings.AuthorEndpoint_V3,
                            _appSettings.DistributorsEndpoint_V1,
                            _appSettings.DistributorsEndpoint_V3));
                    var caPath = _authorities.SamsungFolder;
                    // Core emits progress as localization keys; the sheet localizes them and moves on.
                    var certProgress = sheet.Progress(progress);

                    // Union of DUIDs the (re)generated distributor cert should cover: this TV first,
                    // then manual entries, then already-covered ones, capped at Samsung's per-cert limit.
                    var duids = BuildDistributorDuids(deviceInfo.Duid, manualDuids, coveredDuids, progress);

                    if (needsFullProfile)
                    {
                        (authorp12, distributorp12, p12Password) = await certificateService.GenerateProfileAsync(
                            duids: duids,
                            accessToken: auth.access_token,
                            userId: auth.userId,
                            userEmail: auth.inputEmailID,
                            outputPath: jelly2SamsDir,
                            caPath: caPath,
                            level: requestedLevel,
                            progress: certProgress);
                    }
                    else
                    {
                        // Reuse the existing author cert; only the distributor cert is regenerated.
                        distributorp12 = await certificateService.RegenerateDistributorAsync(
                            certDir: jelly2SamsDir,
                            duids: duids,
                            accessToken: auth.access_token,
                            userId: auth.userId,
                            userEmail: auth.inputEmailID,
                            caPath: caPath,
                            level: requestedLevel,
                            progress: certProgress);
                        authorp12 = Path.Combine(jelly2SamsDir, Constants.Certificate.AuthorFileName);
                        p12Password = (await File.ReadAllTextAsync(
                            Path.Combine(jelly2SamsDir, Constants.Certificate.PasswordFileName))).Trim();
                    }

                    PackageCertificate = AutoCertProfileName(requestedLevel);
                    await sheet.FinishAsync();
                }
                catch (Exception ex)
                {
                    return await EndCertificateRunAsync(sheet, ex);
                }
            }
            else
            {
                // Reuse in place. For the auto-generated cert, use THIS level's folder, the stored
                // ChosenCertificates path may point at the other level from a previous install.
                // Bundled/imported certs keep using their stored path.
                bool reuseAutoCert = !isBundledJellyfin && autoMode;
                var certDir = reuseAutoCert
                    ? jelly2SamsDir
                    : Path.GetDirectoryName(chosenCertificate!.File)!;
                authorp12 = Path.Combine(certDir, Constants.Certificate.AuthorFileName);
                distributorp12 = Path.Combine(certDir, Constants.Certificate.DistributorFileName);
                p12Password = File.ReadAllText(Path.Combine(certDir, Constants.Certificate.PasswordFileName)).Trim();
                PackageCertificate = reuseAutoCert ? AutoCertProfileName(requestedLevel) : selectedCertificate;
            }

            // Handle permit install for older Tizen versions (thresholds shared with Core).
            await Apps2Samsung.Sdb.TizenPermitInstall.EnsureAsync(
                _sdb, tvIpAddress, deviceInfo.TizenVersion, deviceInfo.SdkToolPath,
                Path.Combine(Path.GetDirectoryName(authorp12)!, Constants.Certificate.DeviceProfileFileName));

            return new CertificateResult
            {
                Success = true,
                AuthorP12 = authorp12,
                DistributorP12 = distributorp12,
                P12Password = p12Password
            };
        }

        /// <summary>
        /// The certificates that come with the app have end dates (#1153). When the set this install
        /// needs has ended, the user is told which and when, and asked before new ones are downloaded.
        /// Null to go on (downloaded, or the user wants to try with the old ones, or chose a Samsung
        /// certificate, which switches the setting); a result when the install stops here.
        /// </summary>
        private async Task<CertificateResult?> CheckAuthoritiesAsync(AuthoritySet set, DeviceInfo deviceInfo, CancellationToken cancellationToken)
        {
            if (_authorities.Ended(set, DateTimeOffset.UtcNow) is not { } ended)
                return null;

            Trace.WriteLine($"[Cert] The {set} authority {ended.FileName} ended on {ended.EndsAt:u}; asking before a download.");
            var (choice, withEnded) = await _dialogService.ShowAuthoritiesEndedAsync(
                set, ended.EndsAt, deviceInfo.Name, offerSamsung: set == AuthoritySet.Tizen,
                download: () => _authorities.RefreshAsync(_httpClient, set, DateTimeOffset.UtcNow, cancellationToken));
            Trace.WriteLine($"[Cert] {set} authorities ended: {choice}{(withEnded ? " (with the ended ones)" : "")}.");

            switch (choice)
            {
                case Apps2Samsung.Views.AuthorityChoice.Continue:
                    if (withEnded)
                        _authorities.AcceptEnded(set);
                    return null;
                case Apps2Samsung.Views.AuthorityChoice.SamsungInstead:
                    _appSettings.UseSamsungCertificate();
                    return null;
                default:
                    return AuthoritiesStopped();
            }
        }

        /// <summary>
        /// The same question for the certificate shipped for Jellyfin on Tizen 4.1 to 6.x: it ends
        /// with the Tizen authority and cannot be renewed by a download, so the ways on are a Samsung
        /// certificate for this install, trying anyway, or stopping.
        /// </summary>
        private async Task<CertificateResult?> CheckShippedCertificateAsync(DeviceInfo deviceInfo, InstallRun run)
        {
            var shipped = Path.Combine(AppSettings.BundledCertificatePath, Constants.AppIdentifiers.JellyfinAppName);
            if (CertificateProvisioningService.AuthorValidUntil(shipped) is not { } validUntil || validUntil > DateTime.Now)
                return null;
            // Tried anyway earlier in this session: not asked again.
            if (_authorities.Ended(AuthoritySet.Tizen, DateTimeOffset.UtcNow) is null)
                return null;

            Trace.WriteLine($"[Cert] The shipped Jellyfin certificate ended on {validUntil:u}; asking how to go on.");
            var (choice, _) = await _dialogService.ShowAuthoritiesEndedAsync(
                AuthoritySet.Tizen, new DateTimeOffset(validUntil), deviceInfo.Name, offerSamsung: true,
                download: null, nothingToFetchBody: "authShippedEndedBody".Localized());
            Trace.WriteLine($"[Cert] Shipped certificate ended: {choice}.");

            switch (choice)
            {
                case Apps2Samsung.Views.AuthorityChoice.Continue:
                    _authorities.AcceptEnded(AuthoritySet.Tizen);
                    return null;
                case Apps2Samsung.Views.AuthorityChoice.SamsungInstead:
                    run.SkipShippedCertificate = true;
                    return null;
                default:
                    return AuthoritiesStopped();
            }
        }

        private static CertificateResult AuthoritiesStopped() => new()
        {
            Success = false,
            InstallResult = InstallResult.CancelledResult("statusAuthoritiesEndedStopped".Localized()),
        };

        /// <summary>
        /// The Tizen certificate for this install (#988): the profile on disk for the level the
        /// Settings switch means, or a new one made on the spot behind the two-step sheet. Null when
        /// the user chose a Samsung certificate on that sheet: the setting is switched by then and
        /// the caller goes on with the Samsung path.
        /// </summary>
        private async Task<CertificateResult?> HandleTizenCertificateAsync(
            string tvIpAddress,
            DeviceInfo deviceInfo,
            bool requirePartner,
            ProgressCallback? progress,
            CancellationToken cancellationToken,
            InstallRun run)
        {
            var level = Apps2Samsung.Certificate.CertificateKind.TizenLevel(_appSettings.CertificatePreference, requirePartner);
            var store = AppSettings.CertificatePath;
            bool needsNew = run.ForceLogin || !TizenProfileStore.HasProfile(store, level);

            using var sheet = new CertificateSheetRun(_dialogService, deviceInfo.Name, cancellationToken, deviceInfo.Duid);
            try
            {
                if (needsNew)
                {
                    if (!await sheet.OpenTizenAsync(level))
                    {
                        Trace.WriteLine("[Cert] Samsung certificate chosen on the Tizen sheet; switching the setting.");
                        _appSettings.UseSamsungCertificate();
                        return null;
                    }
                    progress?.Invoke(Constants.LocalizationKeys.CreatingCertificateProfile.Localized());
                }

                var profile = await TizenProfileStore.EnsureAsync(store, level, force: run.ForceLogin, progress: sheet.Progress(progress));
                await sheet.FinishAsync();

                PackageCertificate = profile.ProfileName;
                run.TizenProfile = profile;

                // A Tizen profile has no device-profile.xml, so this is a no-op; kept for the day it has.
                await Apps2Samsung.Sdb.TizenPermitInstall.EnsureAsync(
                    _sdb, tvIpAddress, deviceInfo.TizenVersion, deviceInfo.SdkToolPath,
                    Path.Combine(profile.ProfileDir, Constants.Certificate.DeviceProfileFileName));

                return new CertificateResult
                {
                    Success = true,
                    AuthorP12 = profile.AuthorP12,
                    DistributorP12 = profile.DistributorP12,
                    P12Password = profile.Password
                };
            }
            catch (OperationCanceledException ex)
            {
                // Cancel on the sheet: no sign-in was involved, so say the install was cancelled.
                await sheet.EndAsync(ex);
                Trace.WriteLine("[Cert] Tizen certificate sheet cancelled.");
                return new CertificateResult { Success = false, InstallResult = InstallResult.CancelledResult("statusInstallCancelled".Localized()) };
            }
            catch (Exception ex)
            {
                return await EndCertificateRunAsync(sheet, ex);
            }
        }

        // True when a generated author cert exists and is still valid, i.e. we can keep that
        // author identity and only regenerate the distributor cert for a new TV.
        private static bool HasUsableAuthorCert(string certDir)
        {
            try
            {
                var authorP12 = Path.Combine(certDir, Constants.Certificate.AuthorFileName);
                var passwordFile = Path.Combine(certDir, Constants.Certificate.PasswordFileName);
                if (!File.Exists(authorP12) || !File.Exists(passwordFile))
                    return false;

                var password = File.ReadAllText(passwordFile).Trim();
                using var cert = new X509Certificate2(authorP12, password, X509KeyStorageFlags.Exportable);
                return cert.NotAfter.Date >= DateTime.Today;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Author cert check failed for '{certDir}': {ex.Message}");
                return false;
            }
        }

        // Parses a user-entered list of DUIDs (one per line / comma / space separated).
        private static HashSet<string> ParseDuids(string? raw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
                return set;

            foreach (var part in raw.Split(new[] { '\n', '\r', ',', ';', ' ', '\t' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(part);

            return set;
        }

        // DUIDs already covered by the distributor cert in certDir (empty if missing/unreadable).
        private static HashSet<string> GetCoveredDuids(string certDir)
        {
            try
            {
                var passwordFile = Path.Combine(certDir, Constants.Certificate.PasswordFileName);
                if (!File.Exists(passwordFile))
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var password = File.ReadAllText(passwordFile).Trim();
                var distributor = Path.Combine(certDir, Constants.Certificate.DistributorFileName);
                return new CertificateHelper().GetCertificateDuids(distributor, password);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] Could not read covered DUIDs from '{certDir}': {ex.Message}");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // The auto-generated cert profile name for a privilege level, e.g. "Jelly2Sams - Partner".
        // Public and Partner are stored as separate profiles so both can coexist and be reused.
        private static string AutoCertProfileName(CertificatePrivilegeLevel level) =>
            $"{Constants.AppIdentifiers.Jelly2Sams} - {(level == CertificatePrivilegeLevel.Partner ? "Partner" : "Public")}";

        // True for any auto-generated Jelly2Sams profile ("Jelly2Sams", "Jelly2Sams (default)",
        // "Jelly2Sams - Public/Partner"), as opposed to the bundled Jellyfin or a user-imported cert.
        private static bool IsAutoCertName(string? name) =>
            !string.IsNullOrEmpty(name) &&
            name.StartsWith(Constants.AppIdentifiers.Jelly2Sams, StringComparison.OrdinalIgnoreCase);

        // The DUID set a (re)generated distributor cert should cover: this TV first, then manual
        // entries, then already-covered ones, capped at Samsung's per-cert limit (extras dropped).
        private static IReadOnlyCollection<string> BuildDistributorDuids(
            string currentDuid, HashSet<string> manual, HashSet<string> covered, ProgressCallback? progress)
        {
            var ordered = new List<string>();
            void Add(string d)
            {
                // Only ever embed well-formed DUIDs, never a stray error string or typo'd manual entry.
                if (TizenDuid.IsValid(d) && !ordered.Contains(d.Trim(), StringComparer.OrdinalIgnoreCase))
                    ordered.Add(d.Trim());
            }

            Add(currentDuid);
            foreach (var d in manual) Add(d);
            foreach (var d in covered) Add(d);

            int max = Constants.Certificate.MaxDistributorDuids;
            if (ordered.Count > max)
            {
                progress?.Invoke(string.Format(Constants.LocalizationKeys.DuidLimitReached.Localized(), max));
                Trace.WriteLine($"[Cert] DUID count {ordered.Count} exceeds limit {max}; dropping extras.");
                ordered = ordered.Take(max).ToList();
            }

            return ordered;
        }

        #endregion

        #region TV Communication Methods

        public async Task<string> GetTvNameAsync(string tvIpAddress)
        {
            var output = await _sdb.DevicesAsync(tvIpAddress);
            return output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        }

        public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(string tvIpAddress)
        {
            try
            {
                var result = await _sdb.AppsAsync(tvIpAddress);
                return Apps2Samsung.Sdb.TizenInstalledApps.ParseOrThrow(result);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task<TizenDeviceInfo> GetDeviceInfoAsync(string tvIpAddress, bool debugPortOpen, string? localIp = null)
        {
            try
            {
                return await Apps2Samsung.Sdb.TizenDeviceInfoService.GatherAsync(_sdb, tvIpAddress, debugPortOpen, localIp);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task<ProcessResult> UninstallAppAsync(string tvIpAddress, string tizenId)
        {
            try
            {
                return await _sdb.UninstallAsync(tvIpAddress, tizenId);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task<Apps2Samsung.Sdb.UninstallVerdict> UninstallAndCheckAsync(string tvIpAddress, string tizenId)
        {
            try
            {
                return await Apps2Samsung.Sdb.AppUninstall.RunAsync(_sdb, tvIpAddress, tizenId);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task LaunchAppAsync(string tvIpAddress, string tizenId)
        {
            try
            {
                var result = await _sdb.LaunchAsync(tvIpAddress, tizenId);
                if (result.ExitCode != 0)
                    throw new Exception($"Failed to launch app: {result.Error}");
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task StopAppAsync(string tvIpAddress, string tizenId)
        {
            try
            {
                var result = await _sdb.ShellAsync(tvIpAddress, $"0 was_kill {tizenId}");
                if (result.ExitCode != 0)
                {
                    string errorMsg = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    throw new Exception($"Failed to stop app {tizenId}: {errorMsg}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"StopAppAsync failed: {ex}");
                throw;
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        // Debug-mode launch + inspector tunnel live in Core (Sdb/TizenAppDebugger), shared with the
        // mobile head. Only what happens with the local port differs: here it is handed to Chrome's
        // inspect page, which looks for 9222 specifically, hence the fixed port rather than a free one.
        public async Task<(int LocalPort, Apps2Samsung.Sdb.TizenDebugSession ForwardSession)> DebugAppAsync(
            string tvIpAddress, string tizenId, int localPort = 9222)
        {
            var session = await Apps2Samsung.Sdb.TizenAppDebugger.StartAsync(_sdb, tvIpAddress, tizenId, localPort);
            return (session.LocalPort, session);
        }

        public Task<Apps2Samsung.Sdb.TizenDebugSession> DebugServiceAsync(
            string tvIpAddress, string serviceId, Apps2Samsung.Sdb.TizenDebugSession? appSession) =>
            Apps2Samsung.Sdb.TizenAppDebugger.StartAlongsideAsync(_sdb, tvIpAddress, serviceId, appSession);

        public Task<Apps2Samsung.Diagnostics.ServiceEndpointResult> QueryServiceEndpointAsync(
            string tvIpAddress, int port, string path, CancellationToken cancellationToken = default) =>
            Apps2Samsung.Diagnostics.TizenServiceEndpoint.QueryAsync(_sdb, tvIpAddress, port, path, cancellationToken);

        private async Task<(string tizenOs, string sdkToolPath)> FetchCapabilitiesAsync(string tvIpAddress)
        {
            var output = await _sdb.CapabilityAsync(tvIpAddress);
            var caps = Apps2Samsung.Sdb.TizenCapabilities.Parse(output.Output);
            return (caps.PlatformVersion, caps.SdkToolPath);
        }

        private async Task<string> GetTvDuidAsync(string tvIpAddress)
        {
            // Retry the read, old/slow TVs often return an empty reply or drop the connection on the
            // first try. Returns blank on total failure so the caller's IsValid check handles it.
            try
            {
                return await TizenDuidReader.ReadAsync(_sdb, tvIpAddress);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Cert] DUID read failed for {tvIpAddress}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Asks once per run whether a native package may replace the other app that has its id
        /// (#1028). A no stops the install with nothing on the TV touched.
        /// </summary>
        private async Task<bool> ConfirmReplaceAsync(InstallRun run, Apps2Samsung.Install.PackageIdCollision collision)
        {
            if (run.ReplaceConfirmed is { } answered)
                return answered;

            var confirmed = await Dispatcher.UIThread.InvokeAsync(() => _dialogService.ShowConfirmationAsync(
                string.Format("confirmReplaceOtherTitle".Localized(), collision.OccupantName),
                string.Format("confirmReplaceOther".Localized(), collision.OccupantName, collision.PackageName),
                "btnReplace".Localized(),
                Constants.LocalizationKeys.Cancel.Localized()));

            run.ReplaceConfirmed = confirmed;
            Trace.WriteLine($"[PackageId] Replacing '{collision.OccupantName}' with native '{collision.PackageName}' was {(confirmed ? "confirmed" : "declined")}.");
            return confirmed;
        }

        /// <summary>
        /// An older certificate from the automatic backups may be the one the TV knows (#1068):
        /// offered once per run, before the installer removes anything, because it keeps the app's
        /// data on the TV. A yes puts the older profile in the store and hands it to the installer;
        /// when the TV still says no, the profile it replaced goes back.
        /// </summary>
        private async Task<Apps2Samsung.Install.AlternativeCertificate?> OfferOlderCertificateAsync(InstallRun run, string packagePath)
        {
            var profile = PackageCertificate;
            if (run.OlderCertificateTried || string.IsNullOrEmpty(profile))
                return null;
            var older = Apps2Samsung.Certificate.CertificateBackups.OtherAuthors(profile).FirstOrDefault(e => File.Exists(e.Location));
            if (older is null)
                return null;
            run.OlderCertificateTried = true;

            var appTitle = GetPackageAppTitle(packagePath);
            var confirmed = await Dispatcher.UIThread.InvokeAsync(() => _dialogService.ShowConfirmationAsync(
                string.Format("certRestoreTitle".Localized(), appTitle),
                string.Format("certRestoreMessage".Localized(), appTitle, older.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy"), profile),
                "certRestoreAndRetry".Localized(),
                Constants.LocalizationKeys.Cancel.Localized()));
            Trace.WriteLine($"[Install] Restoring {profile} from {older.Location} was {(confirmed ? "accepted" : "declined")}.");
            if (!confirmed)
                return null;

            // Keep the current profile aside; it is in a backup too, but this puts it back without a picker.
            var profileDir = Path.Combine(AppSettings.CertificatePath, profile);
            var aside = Path.Combine(Path.GetTempPath(), "a2s-cert-aside-" + Guid.NewGuid().ToString("N"));

            void PutBack()
            {
                if (!Directory.Exists(aside))
                    return;
                if (Directory.Exists(profileDir))
                    Directory.Delete(profileDir, recursive: true);
                CopyDirectory(aside, profileDir);
            }

            void DropAside()
            {
                try { if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true); } catch { /* temp */ }
            }

            try
            {
                CopyDirectory(profileDir, aside);
                await using (var stream = File.OpenRead(older.Location))
                    Apps2Samsung.Certificate.CertificateArchive.Import(stream, AppSettings.CertificatePath, onlyProfile: profile);

                var password = (await File.ReadAllTextAsync(Path.Combine(profileDir, Constants.Certificate.PasswordFileName))).Trim();
                var signing = new Apps2Samsung.Install.SigningProfile(
                    Path.Combine(profileDir, Constants.Certificate.AuthorFileName),
                    Path.Combine(profileDir, Constants.Certificate.DistributorFileName),
                    password,
                    profileDir);

                return new Apps2Samsung.Install.AlternativeCertificate(signing, helped =>
                {
                    try
                    {
                        if (!helped)
                        {
                            Trace.WriteLine($"[Install] The older {profile} did not help; putting the current one back.");
                            PutBack();
                        }
                    }
                    finally
                    {
                        DropAside();
                    }
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Install] Restoring the older certificate failed: {ex.Message}");
                try { PutBack(); } catch { /* the backup still has it */ }
                DropAside();
                return null;
            }
        }

        /// <summary>
        /// Asks once per run whether the installed copy may be removed. Uninstalling a Tizen package
        /// deletes its data directory (settings, sign-in, local history), so none of the recovery
        /// paths removes anything without this answer (#825). The answer is kept on the run, so a
        /// retry that lands on a second recovery path doesn't ask twice.
        /// </summary>
        private async Task<bool> ConfirmRemovalAsync(InstallRun run, string packagePath)
        {
            if (run.RemovalConfirmed is { } answered)
                return answered;

            var appTitle = GetPackageAppTitle(packagePath);
            var confirmed = await Dispatcher.UIThread.InvokeAsync(() => _dialogService.ShowConfirmationAsync(
                string.Format(Constants.LocalizationKeys.ConfirmRemoveTitle.Localized(), appTitle),
                string.Format(Constants.LocalizationKeys.ConfirmRemove.Localized(), appTitle),
                Constants.LocalizationKeys.RemoveAndInstall.Localized(),
                Constants.LocalizationKeys.Cancel.Localized()));

            run.RemovalConfirmed = confirmed;
            Trace.WriteLine($"[Install] Removing the installed '{appTitle}' was {(confirmed ? "confirmed" : "declined")} by the user.");
            return confirmed;
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        /// <summary>The user kept the installed copy: say so and stop, the TV is untouched.</summary>
        private static InstallResult RemovalDeclined(string packagePath, ProgressCallback? progress)
        {
            var message = string.Format(
                Constants.LocalizationKeys.RemoveDeclined.Localized(),
                GetPackageAppTitle(packagePath));
            progress?.Invoke(message);
            return InstallResult.FailureResult(message);
        }

        /// <summary>
        /// Best-effort display title for the app in a package, derived from the file name
        /// (e.g. "Litefin-1.1.0.wgt" -> "Litefin"). Used in the messages to the user.
        /// </summary>
        private static string GetPackageAppTitle(string packagePath)
            => Path.GetFileNameWithoutExtension(packagePath).Split('-')[0];

        #endregion

        #region Helper Classes

        // Mutable state for ONE InstallPackageAsync call (and its internal retries). Seeded from the
        // user's settings and then flipped freely by the retry logic, so the settings stay exactly as
        // the user left them.
        private sealed class InstallRun
        {
            /// <summary>An older certificate from the automatic backups was offered once in this run (#1068).</summary>
            public bool OlderCertificateTried { get; set; }

            /// <summary>Mint a fresh Samsung profile instead of reusing one (seeded from Force Samsung certificate).</summary>
            public bool ForceLogin { get; set; }

            /// <summary>The shipped certificate has ended and the user chose a Samsung one for this install (#1153).</summary>
            public bool SkipShippedCertificate { get; set; }

            /// <summary>
            /// Whether the user allowed the recovery to remove the installed copy (#825).
            /// Null until asked; seeded true when "Uninstall installed version first" is on.
            /// </summary>
            public bool? RemovalConfirmed { get; set; }

            /// <summary>Where the TV's install percentage goes, for every attempt of this run.</summary>
            public IProgress<int>? InstallProgress { get; init; }

            /// <summary>The package the caller handed in. Read only: never patched, signed or renamed.</summary>
            public string SourcePackage { get; init; } = "";

            /// <summary>The throwaway copy this attempt patches, signs and pushes.</summary>
            public StagedPackage? Working { get; private set; }

            /// <summary>The TV's DUID once it was read; the key of a package-id rename record (#839).</summary>
            public string? TvDuid { get; set; }

            /// <summary>The TV's name once it was read, for the sheets.</summary>
            public string? TvName { get; set; }

            /// <summary>The catalog entry the package is from, which a colliding package's new id is derived from (#1028).</summary>
            public string? CatalogKey { get; init; }

            /// <summary>The rename the first attempt applied, if any; the result says why when another app had the id (#1028).</summary>
            public Apps2Samsung.Packaging.PackageIdRename? Rename { get; set; }

            /// <summary>The answer to "replace the other app that has this native package's id?" (#1028), asked once per run.</summary>
            public bool? ReplaceConfirmed { get; set; }

            /// <summary>Set when this attempt signed with a Tizen certificate (#988), with its profile: a
            /// [118, -12] chain refusal then means the TV only takes a Samsung one.</summary>
            public TizenCertificateProfile? TizenProfile { get; set; }

            /// <summary>
            /// A fresh copy of <see cref="SourcePackage"/> for this attempt, so an attempt never
            /// patches a package the previous one already patched (#702).
            /// </summary>
            public string StageWorkingCopy()
            {
                Working?.Dispose();
                Working = StagedPackage.Create(SourcePackage);
                return Working.FilePath;
            }

            public void Dispose() => Working?.Dispose();
        }

        // The same per-app changes an install makes, without a TV being asked (sign-only and the
        // TV-Store hand-off, #979): Core's, as on the phone and the CLI.
        public Task CustomizeAsync(string tvIpAddress, string packagePath) =>
            _installer.CustomizeAsync(tvIpAddress, packagePath);

        public async Task<CertificatePreparation> PrepareCertificateAsync(string tvIpAddress, CancellationToken cancellationToken = default, bool requirePartner = false)
        {
            var deviceInfo = await GetDeviceInfoAsync(tvIpAddress);
            if (deviceInfo == null)
            {
                await _dialogService.ShowErrorAsync(Constants.LocalizationKeys.TvNameNotFound.Localized());
                return CertificatePreparation.Failed;
            }

            // The shipped authorities have end dates (#1153): the same question as at install time.
            if (Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind)
                && await CheckAuthoritiesAsync(AuthoritySet.Tizen, deviceInfo, cancellationToken) is not null)
                return CertificatePreparation.Failed;

            // A Tizen certificate (#988): made here with no sign-in, or already there. Choosing a
            // Samsung certificate on its sheet switches the setting and goes on below.
            if (Apps2Samsung.Certificate.CertificateKind.IsTizen(_appSettings.CertificateKind))
            {
                var tizenLevel = Apps2Samsung.Certificate.CertificateKind.TizenLevel(
                    _appSettings.CertificatePreference, _appSettings.RequiresPartnerSigning || requirePartner);
                var store = AppSettings.CertificatePath;
                bool needsNew = _appSettings.ForceSamsungLogin || !TizenProfileStore.HasProfile(store, tizenLevel);
                using var tizenSheet = new CertificateSheetRun(_dialogService, deviceInfo.Name, cancellationToken, deviceInfo.Duid);
                try
                {
                    if (needsNew && !await tizenSheet.OpenTizenAsync(tizenLevel))
                    {
                        _appSettings.UseSamsungCertificate();
                    }
                    else
                    {
                        await TizenProfileStore.EnsureAsync(store, tizenLevel, force: _appSettings.ForceSamsungLogin, progress: tizenSheet.Progress(null));
                        await tizenSheet.FinishAsync();
                        return needsNew ? CertificatePreparation.Created : CertificatePreparation.Reused;
                    }
                }
                catch (Exception ex)
                {
                    await EndCertificateRunAsync(tizenSheet, ex);
                    return CertificatePreparation.Failed;
                }
            }

            // The level an install would sign with today (a package that needs Partner switches the
            // choice by itself at install time, so Automatic means Public until then), or Partner
            // when the caller has a package in hand that needs it (sign only, #979).
            var level = Apps2Samsung.Certificate.CertificatePreference.ResolveLevel(
                _appSettings.CertificatePreference, _appSettings.RequiresPartnerSigning || requirePartner);
            if (await CheckAuthoritiesAsync(AuthoritySet.Samsung, deviceInfo, cancellationToken) is not null)
                return CertificatePreparation.Failed;
            var caPath = _authorities.SamsungFolder;
            using var sheet = new CertificateSheetRun(_dialogService, deviceInfo.Name, cancellationToken, deviceInfo.Duid);
            try
            {
                await _provisioning.EnsureAsync(
                    deviceDuid: deviceInfo.Duid,
                    level: level,
                    storePath: AppSettings.CertificatePath,
                    caPath: caPath,
                    manualDuids: ParseDuids(_appSettings.ManualDuids),
                    forceLogin: _appSettings.ForceSamsungLogin,
                    progress: sheet.Progress(null),
                    beforeLoginAsync: sheet.OpenAsync,
                    ct: sheet.Token);
                await sheet.FinishAsync();
                // The sheet only opens when a certificate has to be made.
                return sheet.IsOpen ? CertificatePreparation.Created : CertificatePreparation.Reused;
            }
            catch (Exception ex)
            {
                await EndCertificateRunAsync(sheet, ex);
                return CertificatePreparation.Failed;
            }
        }

        // How a certificate run that didn't finish ends: cancelled quietly, the account page for a
        // Samsung account without an email, anything else on the sheet (or a dialog without one).
        // Every path has told the user already, so the install result is marked as shown.
        private async Task<CertificateResult> EndCertificateRunAsync(CertificateSheetRun sheet, Exception ex)
        {
            await sheet.EndAsync(ex);
            InstallResult result;
            switch (ex)
            {
                case OperationCanceledException:
                    Trace.WriteLine("[Cert] Samsung sign-in cancelled.");
                    result = InstallResult.CancelledResult("statusSignInCancelled".Localized());
                    break;
                case SamsungAccountEmailMissingException:
                    Trace.WriteLine("[Cert] Samsung account has no email address; can't build the distributor CSR.");
                    await _dialogService.ShowAccountEmailMissingAsync();
                    result = InstallResult.ShownFailureResult("statusSamsungAccountNoEmail".Localized());
                    break;
                default:
                    Trace.WriteLine($"[Cert] Provisioning failed: {ex}");
                    if (!sheet.IsOpen)
                        await _dialogService.ShowErrorAsync(ex.Message);
                    result = InstallResult.ShownFailureResult(ex.Message);
                    break;
            }
            return new CertificateResult { Success = false, InstallResult = result };
        }

        /// <summary>
        /// The certificate sheet for one provisioning run (#755). It opens only when a Samsung sign-in
        /// turns out to be needed, so a reused certificate never shows it; Cancel on it cancels
        /// <see cref="Token"/>, which the sign-in waits on.
        /// </summary>
        private sealed class CertificateSheetRun : IDisposable
        {
            private readonly IDialogService _dialogs;
            private readonly string _tvName;
            private readonly string? _tvDuid;
            private readonly CancellationTokenSource _cancel;
            private CertificateSheet? _sheet;

            public CertificateSheetRun(IDialogService dialogs, string tvName, CancellationToken outer, string? tvDuid = null)
            {
                _dialogs = dialogs;
                _tvName = tvName;
                _tvDuid = tvDuid;
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(outer);
            }

            public CancellationToken Token => _cancel.Token;
            public bool IsOpen => _sheet is not null;

            /// <summary>Shows the sheet and waits for Sign in with Samsung; throws on Cancel.</summary>
            public async Task OpenAsync(CertificatePrivilegeLevel level, CancellationToken ct)
            {
                _sheet = await _dialogs.OpenCertificateSheetAsync(_tvName, level, _tvDuid);
                if (_sheet is null)
                    return;
                _sheet.CancelRequested += (_, _) => _cancel.Cancel();
                if (!await _sheet.SignInChosen)
                    _cancel.Cancel();
                _cancel.Token.ThrowIfCancellationRequested();
            }

            /// <summary>
            /// The Tizen sheet (#988): waits for Create the certificate (true) or Use a Samsung
            /// certificate instead (false, the sheet is closed); throws on Cancel.
            /// </summary>
            public async Task<bool> OpenTizenAsync(TizenDistributorLevel level)
            {
                _sheet = await _dialogs.OpenTizenCertificateSheetAsync(_tvName, level);
                if (_sheet is null)
                    return true;
                _sheet.CancelRequested += (_, _) => _cancel.Cancel();
                if (!await _sheet.SignInChosen)
                    _cancel.Cancel();
                _cancel.Token.ThrowIfCancellationRequested();
                if (!_sheet.SamsungInstead)
                    return true;
                _sheet = null;
                return false;
            }

            /// <summary>The certificate service's progress keys: localized for the status line, and
            /// moving the sheet's steps on.</summary>
            public ProgressCallback Progress(ProgressCallback? progress) => key =>
            {
                var text = key.Localized();
                if (CertificateStages.FromProgressKey(key) is { } stage)
                    _sheet?.SetStage(stage);
                _sheet?.Report(text);
                progress?.Invoke(text);
            };

            public Task FinishAsync() => _sheet?.FinishAsync() ?? Task.CompletedTask;

            public async Task EndAsync(Exception ex)
            {
                if (_sheet is null)
                    return;
                if (ex is OperationCanceledException or SamsungAccountEmailMissingException)
                    await _sheet.CloseAsync();
                else
                    _sheet.Fail(ex.Message);
            }

            public void Dispose() => _cancel.Dispose();
        }

        private class DeviceInfo
        {
            public required string Name { get; init; }
            public required string Duid { get; init; }
            public required Version TizenVersion { get; init; }
            public required string SdkToolPath { get; init; }
        }

        private class CertificateResult
        {
            public bool Success { get; init; }
            public string AuthorP12 { get; init; } = string.Empty;
            public string DistributorP12 { get; init; } = string.Empty;
            public string P12Password { get; init; } = string.Empty;
            public InstallResult? InstallResult { get; init; }
        }

        #endregion
    }
}
