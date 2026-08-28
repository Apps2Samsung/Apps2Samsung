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
using Apps2Samsung.Sdb;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
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
        }

        #region Package Download

        // Downloads a .wgt and verifies it is a valid archive before handing it back.
        public async Task<string> DownloadPackageAsync(string downloadUrl)
        {
            var fileName = UrlHelper.GetFileNameFromUrl(downloadUrl);
            var localPath = Path.Combine(AppSettings.DownloadPath, fileName);

            // Reuse a cached copy, but only if it's actually a valid archive: a previous download
            // that was interrupted (network drop, VPN reset, app quit) can leave a truncated file
            // here, and returning it blindly makes the patcher fail later with
            // "End of Central Directory record could not be found". If corrupt, drop and re-download.
            if (File.Exists(localPath))
            {
                if (IsValidZipArchive(localPath))
                    return localPath;

                Trace.WriteLine($"[Download] Cached package is corrupt, re-downloading: {localPath}");
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

                // .wgt is a zip — if the trailer isn't there, the file is truncated/corrupt.
                if (!IsValidZipArchive(tempPath))
                    throw new InvalidDataException("Downloaded package is not a valid .wgt archive (corrupt or incomplete).");

                File.Move(tempPath, localPath, overwrite: true);
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
            bool? wasAlreadyInstalled = null)
        {
            // Record whether the app was already on the TV BEFORE this run (once, on the outer call;
            // recursive retries carry the original value forward). This gates the fresh-install partial
            // cleanup in HandleInstallationResultAsync: we only clear a partial for an app that wasn't
            // there to begin with, never a pre-existing working app. If we can't tell, assume installed
            // (fail safe — never uninstall).
            if (wasAlreadyInstalled is null)
            {
                try
                {
                    var (existing, _) = await CheckForInstalledApp(tvIpAddress, packageUrl);
                    wasAlreadyInstalled = existing;
                }
                catch
                {
                    wasAlreadyInstalled = true;
                }
            }

            try
            {
                // Step 1: Prepare device and check for existing installations
                var prepareResult = await PrepareDeviceAsync(tvIpAddress, packageUrl, progress, cancellationToken);
                if (!prepareResult.Success)
                    return prepareResult;

                // Step 2: Connect and get device information
                progress?.Invoke(Constants.LocalizationKeys.ConnectingToDevice.Localized());

                var deviceInfo = await GetDeviceInfoAsync(tvIpAddress);
                if (deviceInfo == null)
                {
                    progress?.Invoke(Constants.LocalizationKeys.TvNameNotFound.Localized());
                    return InstallResult.FailureResult(Constants.LocalizationKeys.TvNameNotFound.Localized());
                }

                // Step 3: Check the WGT is compatible with the TV's Tizen version (shared Core check,
                // so the mobile head applies the exact same gate).
                var requiredTizenVersion = await WgtManifest.ReadRequiredVersionAsync(packageUrl);
                if (WgtManifest.RequiresNewerTizen(deviceInfo.TizenVersion, requiredTizenVersion))
                {
                    progress?.Invoke(Constants.LocalizationKeys.IncompatiblePackage.Localized());
                    Trace.WriteLine($"Package requires Tizen {requiredTizenVersion} but device has {deviceInfo.TizenVersion}");
                    return InstallResult.FailureResult(string.Format(Constants.LocalizationKeys.IncompatiblePackageDetailed.Localized(), requiredTizenVersion, deviceInfo.TizenVersion));
                }

                // Step 4: Handle certificate selection/generation
                var certificateResult = await HandleCertificateAsync(
                    tvIpAddress,
                    deviceInfo,
                    packageUrl,
                    progress,
                    cancellationToken,
                    onSamsungLoginStarted);

                if (!certificateResult.Success)
                    return certificateResult.InstallResult;

                // Step 4b: the certificate we're about to sign with must already be inside its
                // validity window. Tizen checks the signature against the TV's clock and refuses a
                // certificate whose start date is still in the future ("Certificate in signature is
                // not valid yet"); re-signing or overwriting can't help — only waiting. So hold the
                // install here and count down to the start date instead of pushing a doomed package.
                // Loops because the certificate is re-checked after the wait: only a genuinely valid
                // certificate gets past this point.
                while (certificateResult.RequiresResign)
                {
                    var validity = CertificateValidity.CheckSigningProfile(
                        certificateResult.AuthorP12,
                        certificateResult.DistributorP12,
                        certificateResult.P12Password);

                    if (!validity.IsNotYetValid)
                        break;

                    var validFrom = validity.ValidFromLocal!.Value;
                    var waitMessage = string.Format(
                        Constants.LocalizationKeys.CertificateNotYetValid.Localized(),
                        validFrom.ToString("f", CultureInfo.CurrentCulture));

                    progress?.Invoke(Constants.LocalizationKeys.CertificateWaiting.Localized());
                    var proceed = await _dialogService.ShowCertificateCountdownAsync(
                        Constants.LocalizationKeys.CertificateNotYetValidTitle.Localized(),
                        waitMessage,
                        validFrom);

                    if (!proceed)
                    {
                        // Cancelled during the wait — a deliberate stop, not a failed install.
                        _appSettings.TryOverwrite = false;
                        progress?.Invoke(Constants.LocalizationKeys.CertificateWaitCancelled.Localized());
                        return InstallResult.FailureResult(
                            Constants.LocalizationKeys.CertificateWaitCancelled.Localized());
                    }
                }

                // Step 5: Apply package configuration. Every matching patcher runs, in registration
                // order, so app-specific patchers (channels/oblong) compose with the generic
                // custom-icon patcher (registered last, so it overrides built-in icons).
                foreach (var patcher in _packagePatchers.Where(p => p.CanHandle(packageUrl)))
                {
                    Trace.WriteLine($"Applying configuration via {patcher.GetType().Name}");
                    await patcher.ApplyAsync(packageUrl);
                }

                // Step 6: Resign package if needed
                if (certificateResult.RequiresResign)
                {
                    Trace.WriteLine("Resigning package with new certificate");
                    progress?.Invoke(Constants.LocalizationKeys.PackageAndSign.Localized());
                    var resignResults = await ResignPackageAsync(
                        packageUrl,
                        certificateResult.AuthorP12,
                        certificateResult.DistributorP12,
                        certificateResult.P12Password);

                    if (resignResults.ExitCode != 0 || resignResults.Output.Contains(Constants.TizenErrorCodes.ResignFailed))
                    {
                        Trace.WriteLine($"Resign output: {resignResults.Output}");
                        progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());
                        _appSettings.TryOverwrite = false;
                        return InstallResult.FailureResult(string.Format("statusResignFailed".Localized(), resignResults.Output));
                    }
                }

                // Step 7: Install package and handle results
                progress?.Invoke(Constants.LocalizationKeys.InstallingPackage.Localized());

                return await HandleInstallationResultAsync(
                    packageUrl,
                    tvIpAddress,
                    deviceInfo.SdkToolPath,
                    progress,
                    cancellationToken,
                    onSamsungLoginStarted,
                    wasAlreadyInstalled.Value);
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Installation error: {ex}");
                _appSettings.TryOverwrite = false;

                // Surface the innermost cause too — outer messages like "The SSL connection could
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

        private async Task<InstallResult> PrepareDeviceAsync(
            string tvIpAddress,
            string packageUrl,
            ProgressCallback? progress,
            CancellationToken cancellationToken)
        {
            if (_appSettings.TryOverwrite)
                return InstallResult.SuccessResult();

            progress?.Invoke(Constants.LocalizationKeys.DiagnoseTv.Localized());

            bool canDelete = await GetTvDiagnoseAsync(tvIpAddress);
            var (alreadyInstalled, appId) = await CheckForInstalledApp(tvIpAddress, packageUrl);
            Trace.WriteLine($"Diagnose canDelete: {canDelete}, alreadyInstalled: {alreadyInstalled}, appId: {appId}");

            if (!canDelete && alreadyInstalled)
            {
                var message = string.Format(
                    Constants.LocalizationKeys.AlreadyInstalled.Localized(),
                    GetPackageAppTitle(packageUrl));
                progress?.Invoke(message);
                return InstallResult.FailureResult(message);
            }

            if (canDelete && alreadyInstalled)
            {
                if (_appSettings.DeletePreviousInstall)
                {
                    progress?.Invoke(Constants.LocalizationKeys.DeleteExistingVersion.Localized());
                    var uninstallResult = await UninstallPackageAsync(tvIpAddress, appId!);
                    Trace.WriteLine($"Uninstall output: {uninstallResult.Output}");
                    if (uninstallResult.Output.Contains(Constants.TizenErrorCodes.NotInstalled))
                        return InstallResult.SuccessResult();


                    var (stillInstalled, _) = await CheckForInstalledApp(tvIpAddress, packageUrl);
                    if (stillInstalled)
                    {
                        progress?.Invoke(Constants.LocalizationKeys.DeleteExistingFailed.Localized());
                        return InstallResult.FailureResult(Constants.LocalizationKeys.DeleteExistingFailed.Localized());
                    }

                    progress?.Invoke(Constants.LocalizationKeys.DeleteExistingSuccess.Localized());
                }
                else
                {
                    progress?.Invoke(Constants.LocalizationKeys.DeleteExistingNotAllowed.Localized());
                    return InstallResult.FailureResult(Constants.LocalizationKeys.DeleteExistingNotAllowed.Localized());
                }
            }

            return InstallResult.SuccessResult();
        }

        private async Task<DeviceInfo?> GetDeviceInfoAsync(string tvIpAddress)
        {
            string tvName = await GetTvNameAsync(tvIpAddress);
            if (string.IsNullOrEmpty(tvName))
                return null;

            string tvDuid = await GetTvDuidAsync(tvIpAddress);
            // Reject a malformed DUID (e.g. an SDB transport-error string) so it never ends up baked
            // into the certificate's device-id SAN.
            if (!TizenDuid.IsValid(tvDuid))
            {
                Trace.WriteLine($"[Cert] Invalid TV DUID read from {tvIpAddress}: '{tvDuid}'");
                return null;
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
            string packageUrl,
            ProgressCallback? progress,
            CancellationToken cancellationToken,
            Action? onSamsungLoginStarted)
        {
            var fileName = Path.GetFileName(packageUrl);
            bool manualResign = !fileName.Contains(Constants.AppIdentifiers.JellyfinAppName, StringComparison.OrdinalIgnoreCase);

            Version certVersion = new(Constants.TizenVersions.CertificateRequired);
            Version pushVersion = new(Constants.TizenVersions.PushInstallMax);

            bool requiresResign = deviceInfo.TizenVersion >= certVersion ||
                                  deviceInfo.TizenVersion <= pushVersion ||
                                  !string.IsNullOrEmpty(_appSettings.JellyfinIP) ||
                                  _appSettings.ForceSamsungLogin ||
                                  manualResign;

            if (!requiresResign)
            {
                return new CertificateResult { Success = true, RequiresResign = false };
            }

            string certDuid = _appSettings.ChosenCertificates?.Duid ?? string.Empty;
            string selectedCertificate = _appSettings.Certificate;

            // Handle intermediate Tizen versions that don't need Samsung cert
            if (deviceInfo.TizenVersion < certVersion &&
                deviceInfo.TizenVersion > pushVersion &&
                selectedCertificate == Constants.AppIdentifiers.Jelly2SamsDefault)
            {
                selectedCertificate = Constants.AppIdentifiers.JellyfinAppName;
                _appSettings.Certificate = selectedCertificate;
                _appSettings.ChosenCertificates = new ExistingCertificates
                {
                    Name = Constants.AppIdentifiers.JellyfinAppName,
                    Duid = deviceInfo.Duid,
                    File = Path.Combine(AppSettings.BundledCertificatePath, Constants.AppIdentifiers.JellyfinAppName, Constants.Certificate.AuthorFileName)
                };
            }

            string authorp12, distributorp12, p12Password;

            // Public and Partner distributor certs are kept as separate profiles ("Jelly2Sams - Public"
            // / "Jelly2Sams - Partner") so both can coexist and each is reused independently — switching
            // level never clobbers the other. The requested level comes from the toggle (later also
            // per-package via the manifest).
            // Partner if the global toggle is on, OR the selected package's manifest declares it, OR
            // the package itself declares a partner-level privilege (e.g. vpnservice in a .wgt's
            // config.xml, drminfo in a .tpk's tizen-manifest.xml) — the automatic binding: a package
            // that needs a restricted API must declare it, so we don't track cert levels per package.
            var partnerPrivilege = Apps2Samsung.Packaging.WgtPrivileges.FindPartnerPrivilege(packageUrl);

            // The package can only be installed Partner-signed, and there's no Partner certificate yet:
            // turn the Settings toggle on so one is actually created (and so the UI matches what we
            // sign with) instead of letting the install fail on the TV with MISMATCHED_PRIVILEGE_LEVEL.
            if (partnerPrivilege is not null && !_appSettings.PartnerSigning &&
                !CertificateProvisioningService.HasProfile(AppSettings.CertificatePath, CertificatePrivilegeLevel.Partner))
            {
                Trace.WriteLine($"[Cert] Auto-enabling Partner signing: package declares '{partnerPrivilege}'.");
                _appSettings.EnablePartnerSigning();
                progress?.Invoke(Constants.LocalizationKeys.PartnerSigningAutoEnabled.Localized());
            }

            var requestedLevel = (_appSettings.PartnerSigning
                                  || _appSettings.RequiresPartnerSigning
                                  || partnerPrivilege is not null)
                ? CertificatePrivilegeLevel.Partner
                : CertificatePrivilegeLevel.Public;
            var jelly2SamsDir = Path.Combine(AppSettings.CertificatePath, AutoCertProfileName(requestedLevel));
            bool hasAuthor = HasUsableAuthorCert(jelly2SamsDir);

            // Older Tizen TVs use the shipped "Jellyfin" certificate (set just above) — it's always
            // present and never regenerated, so it bypasses all Samsung-login / regeneration logic
            // and is just reused from disk (unless the user forces a fresh login).
            bool isBundledJellyfin = selectedCertificate == Constants.AppIdentifiers.JellyfinAppName;

            // "Auto" mode = the app drives cert selection itself: no real pick, the "(default)"
            // placeholder, or one of our generated "Jelly2Sams[ - Public/Partner]" profiles. In auto
            // mode we REUSE an existing valid profile for the requested level rather than logging in
            // again. Crucially, the "(default)"/empty placeholder must NOT force a fresh Samsung login
            // when a usable level cert is already on disk — that placeholder was the cause of the
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
                var caPath = Path.Combine(AppSettings.ProfilePath, "ca");
                // Core emits progress as localization keys; localize them here at the boundary.
                ProgressCallback? certProgress = progress is null ? null : new ProgressCallback(k => progress(k.Localized()));
                try
                {
                    var profile = await _provisioning.EnsureAsync(
                        deviceDuid: deviceInfo.Duid,
                        level: requestedLevel,
                        storePath: AppSettings.CertificatePath,
                        caPath: caPath,
                        manualDuids: ParseDuids(_appSettings.ManualDuids),
                        forceLogin: _appSettings.ForceSamsungLogin,
                        onLoginStarting: () =>
                        {
                            progress?.Invoke(Constants.LocalizationKeys.SamsungLogin.Localized());
                            onSamsungLoginStarted?.Invoke();
                        },
                        progress: certProgress,
                        ct: cancellationToken);

                    PackageCertificate = profile.ProfileName;
                    _appSettings.Certificate = profile.ProfileName;
                    _appSettings.ChosenCertificates = new ExistingCertificates
                    {
                        Name = profile.ProfileName,
                        Duid = deviceInfo.Duid,
                        File = profile.AuthorP12
                    };
                    _appSettings.Save();

                    // Permit-install for older Tizen versions (thresholds shared with Core).
                    await Apps2Samsung.Sdb.TizenPermitInstall.EnsureAsync(
                        _sdb, tvIpAddress, deviceInfo.TizenVersion, deviceInfo.SdkToolPath,
                        Path.Combine(Path.GetDirectoryName(profile.AuthorP12)!, Constants.Certificate.DeviceProfileFileName));

                    return new CertificateResult
                    {
                        Success = true,
                        RequiresResign = true,
                        AuthorP12 = profile.AuthorP12,
                        DistributorP12 = profile.DistributorP12,
                        P12Password = profile.Password
                    };
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Cert] Provisioning failed: {ex.Message}");
                    await _dialogService.ShowErrorAsync(ex.Message);
                    return new CertificateResult
                    {
                        Success = false,
                        InstallResult = InstallResult.FailureResult(ex.Message)
                    };
                }
            }

            // A full Samsung profile (fresh keypair + new author cert) is only needed when the user
            // forces a login, or there's genuinely no usable author cert for this level yet (first run
            // for the level, or it's missing/expired). Because the folder is level-specific, switching
            // Public<->Partner naturally has no author yet and generates a fresh profile without
            // touching the other level.
            bool needsFullProfile = _appSettings.ForceSamsungLogin ||
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
                progress?.Invoke(Constants.LocalizationKeys.SamsungLogin.Localized());
                onSamsungLoginStarted?.Invoke();

                SamsungAuth auth = await _login.LoginAsync(cancellationToken);

                if (string.IsNullOrEmpty(auth.access_token))
                {
                    await _dialogService.ShowErrorAsync("statusSamsungAuthFailed".Localized());
                    return new CertificateResult
                    {
                        Success = false,
                        InstallResult = InstallResult.FailureResult("statusAuthFailed".Localized())
                    };
                }

                progress?.Invoke(Constants.LocalizationKeys.CreatingCertificateProfile.Localized());
                var certificateService = new TizenCertificateService(
                    _httpClient,
                    new CertificateEndpoints(
                        _appSettings.AuthorEndpoint_V3,
                        _appSettings.DistributorsEndpoint_V1,
                        _appSettings.DistributorsEndpoint_V3));
                var caPath = Path.Combine(AppSettings.ProfilePath, "ca");
                // Core emits progress as localization keys; localize them here at the boundary.
                ProgressCallback? certProgress = progress is null ? null : new ProgressCallback(k => progress(k.Localized()));

                // Union of DUIDs the (re)generated distributor cert should cover: this TV first,
                // then manual entries, then already-covered ones — capped at Samsung's per-cert limit.
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

                var profileName = AutoCertProfileName(requestedLevel);
                PackageCertificate = profileName;
                _appSettings.Certificate = profileName;
                _appSettings.ChosenCertificates = new ExistingCertificates
                {
                    Name = profileName,
                    Duid = deviceInfo.Duid,
                    File = authorp12
                };
                _appSettings.Save();
            }
            else
            {
                // Reuse in place. For the auto-generated cert, use THIS level's folder — the stored
                // ChosenCertificates path may point at the other level from a previous install.
                // Bundled/imported certs keep using their stored path.
                bool reuseAutoCert = !isBundledJellyfin && autoMode;
                var certDir = reuseAutoCert
                    ? jelly2SamsDir
                    : Path.GetDirectoryName(_appSettings.ChosenCertificates!.File)!;
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
                RequiresResign = true,
                AuthorP12 = authorp12,
                DistributorP12 = distributorp12,
                P12Password = p12Password
            };
        }

        // True when a generated author cert exists and is still valid — i.e. we can keep that
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
        // "Jelly2Sams - Public/Partner") — as opposed to the bundled Jellyfin or a user-imported cert.
        private static bool IsAutoCertName(string? name) =>
            !string.IsNullOrEmpty(name) &&
            name.StartsWith(Constants.AppIdentifiers.Jelly2Sams, StringComparison.OrdinalIgnoreCase);

        // The DUID set a (re)generated distributor cert should cover: this TV first, then manual
        // entries, then already-covered ones — capped at Samsung's per-cert limit (extras dropped).
        private static IReadOnlyCollection<string> BuildDistributorDuids(
            string currentDuid, HashSet<string> manual, HashSet<string> covered, ProgressCallback? progress)
        {
            var ordered = new List<string>();
            void Add(string d)
            {
                // Only ever embed well-formed DUIDs — never a stray error string or typo'd manual entry.
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

        #region Installation Result Handling

        private async Task<InstallResult> HandleInstallationResultAsync(
            string packageUrl,
            string tvIpAddress,
            string sdkToolPath,
            ProgressCallback? progress,
            CancellationToken cancellationToken,
            Action? onSamsungLoginStarted,
            bool wasAlreadyInstalled)
        {
            // Best-effort cleanup of a partial left behind by a FRESH failed install. Guarded on
            // wasAlreadyInstalled: only clear a package that wasn't on the TV before this run — never
            // uninstall a pre-existing working app on a failed reinstall. Swallows all errors.
            async Task ClearPartialIfFresh()
            {
                if (wasAlreadyInstalled)
                    return;
                try
                {
                    var pkgId = await WgtManifest.ReadPackageIdAsync(packageUrl);
                    if (!string.IsNullOrWhiteSpace(pkgId))
                        await _sdb.UninstallAsync(tvIpAddress, pkgId!);
                }
                catch { /* best-effort */ }
            }

            var installResults = await InstallPackageOnDeviceAsync(tvIpAddress, packageUrl, sdkToolPath);

            // Transport / connection failure (e.g. "Unable to read data from the transport
            // connection: Connection reset by peer"). Environmental — a VPN/proxy/firewall on
            // the host capturing the route to the TV, or an unstable Wi-Fi link — not a packaging
            // problem, so retrying or overwriting can't help. Fail fast with an actionable hint
            // instead of looping a re-sign+re-push over the same broken route.
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsTransportLost(installResults.Output))
            {
                _appSettings.TryOverwrite = false;
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());
                Trace.WriteLine($"[Install] Transport connection lost to {tvIpAddress}: {installResults.Output}");
                return InstallResult.FailureResult(Constants.LocalizationKeys.ConnectionInterrupted.Localized());
            }

            // Handle insufficient space error
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsInsufficientSpace(installResults.Output))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    Trace.WriteLine("Installation failed, insufficient space! retrying with remove previous version");
                    _appSettings.TryOverwrite = false;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, wasAlreadyInstalled);
                }

                _appSettings.TryOverwrite = false;
                Trace.WriteLine("Installation failed, insufficient space!");
                return InstallResult.FailureResult(string.Format("statusInstallationFailedDetail".Localized(),
                    Constants.LocalizationKeys.InsufficientSpace.Localized()));
            }

            // Handle certificate mismatch: the installed copy was signed with a different
            // certificate, so Tizen refuses to overwrite it. The only fix is removing the old copy.
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsCertificateMismatch(installResults.Output))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                // On TVs that allow SDB uninstall, remove the old copy and reinstall automatically.
                if (_appSettings.TryOverwrite && await GetTvDiagnoseAsync(tvIpAddress))
                {
                    _appSettings.TryOverwrite = false;
                    _appSettings.ForceSamsungLogin = true;
                    _appSettings.DeletePreviousInstall = true;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, wasAlreadyInstalled);
                }

                // Overwrite can't help and the TV can't remove it over USB -> tell the user to delete it manually.
                _appSettings.TryOverwrite = false;
                var certMessage = string.Format(
                    Constants.LocalizationKeys.CertificateMismatch.Localized(),
                    GetPackageAppTitle(packageUrl));
                progress?.Invoke(certMessage);
                return InstallResult.FailureResult(certMessage);
            }

            // Handle API-version incompatibility ([118, -4] "Operation not allowed"): the package
            // targets a higher Tizen API level than this TV supports. Not a certificate or privilege
            // problem, and re-signing/overwriting can't help — the TV simply can't run this build.
            // Give the user a clear reason instead of a raw error code.
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsApiVersionMismatch(installResults.Output))
            {
                _appSettings.TryOverwrite = false;
                var apiMessage = Constants.LocalizationKeys.ApiVersionMismatch.Localized();
                progress?.Invoke(apiMessage);
                Trace.WriteLine($"[Install] API-version incompatibility ([118, -4]) on {tvIpAddress}: {installResults.Output}");
                return InstallResult.FailureResult(apiMessage);
            }

            // Handle package ID conflict error. Note: a service-component incompatibility
            // (the common cause on older TVs) is already handled before install in Step 3b,
            // so reaching here generally means a genuine id/config conflict.
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsPackageIdConflict(installResults.Output))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    _appSettings.TryOverwrite = false;
                    // Give the package a fresh random id and retry. Only retry if the id was actually
                    // rewritten — if the config couldn't be read/modified (previously silent for any
                    // non-".Jellyfin" variant like LiteFin, #400), retrying would hit the identical
                    // [118] conflict, so fall through to a clear failure instead.
                    if (await WgtConfigEditor.RandomizePackageIdAsync(packageUrl))
                        return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, wasAlreadyInstalled);
                }

                _appSettings.TryOverwrite = false;
                return InstallResult.FailureResult(string.Format("statusInstallationFailedDetail".Localized(),
                    Constants.LocalizationKeys.ModifyConfigRequired.Localized()));
            }

            // Handle generic failure
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IsGenericFailure(installResults.Output))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    _appSettings.TryOverwrite = false;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, wasAlreadyInstalled);
                }

                _appSettings.TryOverwrite = false;
                // Retries exhausted on a generic failure — clear any partial left by a fresh install.
                await ClearPartialIfFresh();
                return InstallResult.FailureResult(string.Format("statusInstallationFailedDetail".Localized(), installResults.Output));
            }

            // Handle success
            if (Apps2Samsung.Sdb.TizenInstallDiagnostics.IndicatesSuccess(installResults.Output))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationSuccessful.Localized());

                if (_appSettings.OpenAfterInstall)
                {
                    string tvAppId = await GetInstalledAppId(tvIpAddress, GetPackageAppTitle(packageUrl));
                    _ = Task.Run(async () =>
                    {
                        await _sdb.LaunchAsync(tvIpAddress, tvAppId);
                    });
                }

                return InstallResult.SuccessResult();
            }

            // Unknown result - retry if possible
            progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

            if (_appSettings.TryOverwrite)
            {
                _appSettings.TryOverwrite = false;
                return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted, wasAlreadyInstalled);
            }

            _appSettings.TryOverwrite = false;
            // Retries exhausted on an unknown result — clear any partial left by a fresh install.
            await ClearPartialIfFresh();
            return InstallResult.FailureResult(string.Format("statusInstallationFailedDetail".Localized(), installResults.Output));
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
                return Apps2Samsung.Sdb.TizenInstalledApps.Parse(result?.Output);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        public async Task<TizenDeviceInfo> GetDeviceInfoAsync(string tvIpAddress, bool debugPortOpen)
        {
            try
            {
                return await Apps2Samsung.Sdb.TizenDeviceInfoService.GatherAsync(_sdb, tvIpAddress, debugPortOpen);
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

        public async Task<(int LocalPort, IAsyncDisposable ForwardSession)> DebugAppAsync(string tvIpAddress, string tizenId)
        {
            try
            {
                var result = await _sdb.ShellAsync(tvIpAddress, $"0 debug {tizenId}");
                if (result.ExitCode != 0)
                {
                    string errorMsg = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    throw new Exception($"Failed to start debug mode for {tizenId}: {errorMsg}");
                }

                var match = System.Text.RegularExpressions.Regex.Match(result.Output, @"port:\s*(\d+)");
                if (!match.Success)
                {
                    throw new Exception($"Failed to detect the debug port from the device. Output: {result.Output}");
                }
                
                int remotePort = int.Parse(match.Groups[1].Value);
                int localPort = 9222;

                var forwardSession = await _sdb.ForwardAsync(tvIpAddress, localPort, remotePort);
                return (localPort, forwardSession);
            }
            finally
            {
                await _sdb.DisconnectAsync(tvIpAddress);
            }
        }

        private async Task<(string tizenOs, string sdkToolPath)> FetchCapabilitiesAsync(string tvIpAddress)
        {
            var output = await _sdb.CapabilityAsync(tvIpAddress);
            var caps = Apps2Samsung.Sdb.TizenCapabilities.Parse(output.Output);
            return (caps.PlatformVersion, caps.SdkToolPath);
        }

        private async Task<string> GetTvDuidAsync(string tvIpAddress)
        {
            // Retry the read — old/slow TVs often return an empty reply or drop the connection on the
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

        private async Task<bool> GetTvDiagnoseAsync(string tvIpAddress)
        {
            var output = await _sdb.DiagnoseAsync(tvIpAddress);
            var match = RegexPatterns.TizenCapability.AppUninstallFailed.Match(output.Output);
            return !match.Success;
        }

        /// <summary>
        /// Best-effort display/title for the app in a package, derived from the wgt filename
        /// (e.g. "Litefin-1.1.0.wgt" -> "Litefin"). Used for user messages and the
        /// installed-app lookup; matches how <see cref="CheckForInstalledApp"/> searches.
        /// </summary>
        private static string GetPackageAppTitle(string packageUrl)
            => Path.GetFileNameWithoutExtension(packageUrl).Split('-')[0];

        private async Task<(bool isInstalled, string? appId)> CheckForInstalledApp(string tvIpAddress, string packageUrl)
        {
            var result = await _sdb.AppsAsync(tvIpAddress);
            var output = result?.Output ?? string.Empty;

            // Read what the WGT *claims* its app id is (best effort fallback for "no listing" cases)
            var wgtAppId = await WgtManifest.ReadApplicationIdAsync(packageUrl);

            // Case 3: no listing -> assume installed, return WGT app id as best-effort
            if (string.IsNullOrWhiteSpace(output) ||
                output.Contains("Could not retrieve app list", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Remote closed channel", StringComparison.OrdinalIgnoreCase))
            {
                return (true, wgtAppId);
            }

            // Case 1/2: listing returned -> parse TV output
            var baseSearch = GetPackageAppTitle(packageUrl);
            var blockRegex = RegexPatterns.TizenApp.CreateAppBlockByTitleRegex(baseSearch);
            var blockMatch = blockRegex.Match(output);

            // Case 2: listing returned but not present
            if (!blockMatch.Success)
                return (false, null);

            // Case 1: listing returned and present -> TV's id is the uninstall/overwrite truth
            var block = blockMatch.Value;
            var appIdMatch = RegexPatterns.TizenApp.AppTizenId.Match(block);
            var tvAppId = appIdMatch.Success ? appIdMatch.Groups[1].Value.Trim() : null;

            // If we matched by title but ID isn't matching Config ID (jellyfin-secondary)
            Debug.WriteLine($"TV APP ID: {tvAppId} - CONFIG APP ID: {wgtAppId}");
            Trace.WriteLine($"TV APP ID: {tvAppId} - CONFIG APP ID: {wgtAppId}");
            if (tvAppId != wgtAppId)
                return (false, null);

            // If we matched by title but couldn't parse ID, fall back to WGT ID
            return (true, !string.IsNullOrWhiteSpace(tvAppId) ? tvAppId : wgtAppId);
        }

        private async Task<string> GetInstalledAppId(string tvIpAddress, string appTitle)
        {
            var output = await _sdb.AppsAsync(tvIpAddress);
            string appsOutput = output.Output ?? string.Empty;

            var blockRegex = RegexPatterns.TizenApp.CreateAppBlockByTitleRegex(appTitle);
            var blockMatch = blockRegex.Match(appsOutput);

            if (!blockMatch.Success)
                return string.Empty;

            string block = blockMatch.Value;
            var appIdMatch = RegexPatterns.TizenApp.AppTizenIdWithDelimiter.Match(block);

            return appIdMatch.Success ? appIdMatch.Groups[1].Value.Trim() : string.Empty;
        }

        #endregion

        #region Package Operations

        private async Task<ProcessResult> ResignPackageAsync(string packagePath, string authorP12, string distributorP12, string certPass)
        {
            return await _sdb.ResignAsync(packagePath, authorP12, distributorP12, certPass);
        }

        private async Task<ProcessResult> InstallPackageOnDeviceAsync(string tvIpAddress, string packagePath, string sdkToolPath)
        {
            return await _sdb.InstallAsync(tvIpAddress, packagePath, sdkToolPath);
        }

        private async Task<ProcessResult> UninstallPackageAsync(string tvIpAddress, string packageId)
        {
            return await _sdb.UninstallAsync(tvIpAddress, packageId);
        }

        #endregion

        #region Helper Classes

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
            public bool RequiresResign { get; init; }
            public string AuthorP12 { get; init; } = string.Empty;
            public string DistributorP12 { get; init; } = string.Empty;
            public string P12Password { get; init; } = string.Empty;
            public InstallResult? InstallResult { get; init; }
        }

        #endregion
    }
}
