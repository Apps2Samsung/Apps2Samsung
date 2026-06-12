using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class TizenInstallerService : ITizenInstallerService
    {
        private readonly HttpClient _httpClient;
        private readonly IDialogService _dialogService;
        private readonly AppSettings _appSettings;
        private readonly IEnumerable<IPackagePatcher> _packagePatchers;
        private readonly ProcessHelper _processHelper;

        public string? TizenSdbPath { get; private set; }
        public string? PackageCertificate { get; set; }

        public TizenInstallerService(
            HttpClient httpClient,
            IDialogService dialogService,
            AppSettings appSettings,
            IEnumerable<IPackagePatcher> packagePatchers,
            JellyfinApiClient jellyfinApiClient,
            ProcessHelper processHelper)
        {
            _httpClient = httpClient;
            _dialogService = dialogService;
            _appSettings = appSettings;
            _packagePatchers = packagePatchers;
            _processHelper = processHelper;
        }

        #region TizenSdb Management

        public async Task<string> EnsureTizenSdbAvailable()
        {
            string tizenSdbPath = AppSettings.TizenSdbPath;
            if (!Directory.Exists(tizenSdbPath))
            {
                throw new InvalidOperationException(
                    $"Required component missing.\n\nExpected directory:\n{tizenSdbPath}\n\n" +
                    "Please redownload the application."
                );
            }

            var existingFile = Directory.GetFiles(tizenSdbPath, PlatformService.GetTizenSdbSearchPattern()).FirstOrDefault();
            var latestVersion = string.Empty;

            try
            {
                latestVersion = await GetLatestTizenSdbVersionAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to fetch Tizen SDB version: {ex}");
            }

            if (existingFile != null && !ShouldUpdateBinary(existingFile, latestVersion))
            {
                TizenSdbPath = existingFile;
                return TizenSdbPath;
            }

            string downloadedFile = await DownloadTizenSdbAsync();

            if (existingFile != null && File.Exists(existingFile))
            {
                await _processHelper.MakeExecutableAsync(existingFile);
                File.Delete(existingFile);
            }

            string finalPath = Path.Combine(tizenSdbPath, PlatformService.GetTizenSdbFileName(latestVersion));
            File.Move(downloadedFile, finalPath, true);
            await _processHelper.MakeExecutableAsync(finalPath);

            TizenSdbPath = finalPath;
            return TizenSdbPath;
        }

        private static bool ShouldUpdateBinary(string existingFilePath, string latestVersion)
        {
            try
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(existingFilePath);
                var match = RegexPatterns.Version.FileNameVersion.Match(fileNameWithoutExtension);

                if (!match.Success)
                    return true;

                string currentVersion = match.Groups[1].Value;
                return IsVersionGreater(latestVersion, currentVersion);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsVersionGreater(string latestVersion, string currentVersion)
        {
            var latest = Version.TryParse(latestVersion.TrimStart('v'), out var latestVer) ? latestVer : null;
            var current = Version.TryParse(currentVersion.TrimStart('v'), out var currentVer) ? currentVer : null;

            if (latest == null || current == null)
                return false;

            return latest > current;
        }

        private async Task<string> GetLatestTizenSdbVersionAsync()
        {
            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(Constants.Defaults.HttpRequestTimeoutSeconds));

            try
            {
                using var response = await _httpClient.GetAsync(
                    AppSettings.Default.TizenSdb,
                    timeoutCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"GitHub returned {(int)response.StatusCode}");
                }

                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default);
                var firstRelease = releases?.FirstOrDefault();

                if (firstRelease == null)
                    throw new InvalidOperationException("No releases found");

                return firstRelease.TagName ?? Constants.Defaults.TizenSdbDefaultVersion;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("GitHub did not respond in time.");
            }
        }

        public async Task<string> DownloadTizenSdbAsync()
        {
            try
            {
                var json = await _httpClient.GetStringAsync(AppSettings.Default.TizenSdb);
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default);
                var firstRelease = (releases?.FirstOrDefault()) ?? throw new InvalidOperationException("No releases found");
                string nameMatch = PlatformService.GetAssetPlatformIdentifier();

                var matchedAsset = firstRelease.Assets.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(a.FileName) &&
                    a.FileName.Contains(nameMatch, StringComparison.OrdinalIgnoreCase));

                return matchedAsset == null
                    ? throw new InvalidOperationException($"No matching asset found for {nameMatch}")
                    : await DownloadPackageAsync(matchedAsset.DownloadUrl);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new TimeoutException(
                    "GitHub rate limit reached while checking for Tizen SDB.\n\n" +
                    "To avoid this, set a GitHub Personal Access Token (PAT) in settings,\n" +
                    "or set the GITHUB_TOKEN environment variable,\n" +
                    "or install the GitHub CLI (gh) and run 'gh auth login'.\n\n" +
                    "Alternatively, please try again later.",
                    ex
                );
            }
        }

        public async Task<string> DownloadPackageAsync(string downloadUrl)
        {
            var fileName = UrlHelper.GetFileNameFromUrl(downloadUrl);
            var localPath = Path.Combine(AppSettings.DownloadPath, fileName);

            if (File.Exists(localPath))
                return localPath;

            Directory.CreateDirectory(AppSettings.DownloadPath);

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await contentStream.CopyToAsync(fileStream);

            return localPath;
        }

        #endregion

        #region Main Installation Flow

        public async Task<InstallResult> InstallPackageAsync(
            string packageUrl,
            string tvIpAddress,
            CancellationToken cancellationToken,
            ProgressCallback? progress = null,
            Action? onSamsungLoginStarted = null)
        {
            if (TizenSdbPath is null)
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallTizenSdb.Localized());
                await EnsureTizenSdbAvailable();

                if (TizenSdbPath is null)
                {
                    await _dialogService.ShowErrorAsync(Constants.LocalizationKeys.FailedTizenSdb.Localized());
                    return InstallResult.FailureResult(Constants.LocalizationKeys.InstallTizenSdb.Localized());
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

                // Step 3: Check is WGT is compatible with TV's Tizen version
                var requiredTizenVersion = await FileHelper.ReadWgtRequiredVersion(packageUrl);
                if (requiredTizenVersion != null)
                {
                    var requiredVersion = new Version(requiredTizenVersion);
                    if (deviceInfo.TizenVersion < requiredVersion)
                    {
                        progress?.Invoke(Constants.LocalizationKeys.IncompatiblePackage.Localized());
                        Trace.WriteLine($"Package requires Tizen {requiredVersion} but device has {deviceInfo.TizenVersion}");
                        return InstallResult.FailureResult(string.Format(Constants.LocalizationKeys.IncompatiblePackageDetailed.Localized(), requiredTizenVersion, deviceInfo.TizenVersion));
                    }
                }

                // Step 3b: Older TVs (below Tizen 4.0) can't install a bundled background
                // <tizen:service> component — the whole package is rejected with a generic
                // install failed[118] (see #400). Only on those TVs, strip the service so
                // the main app still installs. Newer TVs keep the package untouched.
                var serviceSupport = new Version(Constants.TizenVersions.ServiceComponentSupport);
                if (deviceInfo.TizenVersion < serviceSupport && await FileHelper.WgtContainsService(packageUrl))
                {
                    if (await FileHelper.StripWgtServiceComponent(packageUrl))
                        Trace.WriteLine($"Device Tizen {deviceInfo.TizenVersion} < {serviceSupport}: removed bundled background service so the package can install");
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

                // Step 5: Apply app-specific configuration to the package, if any patcher matches.
                var patcher = _packagePatchers.FirstOrDefault(p => p.CanHandle(packageUrl));
                if (patcher != null)
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
                        return InstallResult.FailureResult($"Package resigning failed: {resignResults.Output}");
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
                    onSamsungLoginStarted);
            }
            catch (Exception ex)
            {
                progress?.Invoke($"Installation error: {ex}");
                _appSettings.TryOverwrite = false;
                return InstallResult.FailureResult(ex.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tvIpAddress))
                    await _processHelper.RunCommandAsync(TizenSdbPath!, $"disconnect {tvIpAddress}");
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
            if (string.IsNullOrEmpty(tvDuid))
                return null;

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
            Version intermediateVersion = new(Constants.TizenVersions.IntermediateVersion);

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

            var jelly2SamsDir = Path.Combine(AppSettings.CertificatePath, Constants.AppIdentifiers.Jelly2Sams);
            bool hasAuthor = HasUsableAuthorCert(jelly2SamsDir);

            // A full Samsung profile (fresh keypair + new author cert) is only needed on first run,
            // when no real cert is selected, when the author cert is missing/expired, or when forced.
            bool needsFullProfile = string.IsNullOrEmpty(selectedCertificate) ||
                                    selectedCertificate == Constants.AppIdentifiers.Jelly2SamsDefault ||
                                    !hasAuthor ||
                                    _appSettings.ForceSamsungLogin;

            // Author cert exists but this TV's DUID isn't what the distributor cert was made for:
            // regenerate ONLY the distributor (reusing the author keypair) so the author identity
            // stays byte-identical and apps already installed on other TVs remain overwritable.
            bool needsDistributorOnly = !needsFullProfile &&
                                        deviceInfo.Duid != certDuid &&
                                        selectedCertificate != Constants.AppIdentifiers.JellyfinAppName;

            if (needsFullProfile || needsDistributorOnly)
            {
                progress?.Invoke(Constants.LocalizationKeys.SamsungLogin.Localized());
                onSamsungLoginStarted?.Invoke();

                SamsungAuth auth = await SamsungLoginService.PerformSamsungLoginAsync(cancellationToken);

                if (string.IsNullOrEmpty(auth.access_token))
                {
                    await _dialogService.ShowErrorAsync("Failed to authenticate with Samsung account.");
                    return new CertificateResult
                    {
                        Success = false,
                        InstallResult = InstallResult.FailureResult("Auth failed.")
                    };
                }

                progress?.Invoke(Constants.LocalizationKeys.CreatingCertificateProfile.Localized());
                var certificateService = new TizenCertificateService(_httpClient, _dialogService);

                if (needsFullProfile)
                {
                    (authorp12, distributorp12, p12Password) = await certificateService.GenerateProfileAsync(
                        duid: deviceInfo.Duid,
                        accessToken: auth.access_token,
                        userId: auth.userId,
                        userEmail: auth.inputEmailID,
                        outputPath: jelly2SamsDir,
                        progress);
                }
                else
                {
                    // Reuse the existing author cert; only the distributor cert is regenerated.
                    distributorp12 = await certificateService.RegenerateDistributorAsync(
                        certDir: jelly2SamsDir,
                        duid: deviceInfo.Duid,
                        accessToken: auth.access_token,
                        userId: auth.userId,
                        userEmail: auth.inputEmailID,
                        progress);
                    authorp12 = Path.Combine(jelly2SamsDir, Constants.Certificate.AuthorFileName);
                    p12Password = (await File.ReadAllTextAsync(
                        Path.Combine(jelly2SamsDir, Constants.Certificate.PasswordFileName))).Trim();
                }

                PackageCertificate = Constants.AppIdentifiers.Jelly2Sams;
                _appSettings.Certificate = PackageCertificate;
                _appSettings.ChosenCertificates = new ExistingCertificates
                {
                    Name = Constants.AppIdentifiers.Jelly2Sams,
                    Duid = deviceInfo.Duid,
                    File = authorp12
                };
                _appSettings.Save();
            }
            else
            {
                var certDir = Path.GetDirectoryName(_appSettings.ChosenCertificates!.File)!;
                authorp12 = Path.Combine(certDir, Constants.Certificate.AuthorFileName);
                distributorp12 = Path.Combine(certDir, Constants.Certificate.DistributorFileName);
                p12Password = File.ReadAllText(Path.Combine(certDir, Constants.Certificate.PasswordFileName)).Trim();
                PackageCertificate = selectedCertificate;
            }

            // Handle permit install for older Tizen versions
            if (deviceInfo.TizenVersion <= pushVersion)
            {
                var deviceProfilePath = Path.Combine(Path.GetDirectoryName(authorp12)!, Constants.Certificate.DeviceProfileFileName);
                var targetPath = deviceInfo.TizenVersion < intermediateVersion
                    ? Constants.Defaults.HomeDeveloperPath
                    : deviceInfo.SdkToolPath;

                await AllowPermitInstall(tvIpAddress, deviceProfilePath, targetPath);
            }

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

        #endregion

        #region Installation Result Handling

        private async Task<InstallResult> HandleInstallationResultAsync(
            string packageUrl,
            string tvIpAddress,
            string sdkToolPath,
            ProgressCallback? progress,
            CancellationToken cancellationToken,
            Action? onSamsungLoginStarted)
        {
            var installResults = await InstallPackageOnDeviceAsync(tvIpAddress, packageUrl, sdkToolPath);

            // Handle insufficient space error
            if (installResults.Output.Contains(Constants.TizenErrorCodes.DownloadFailed116))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    Trace.WriteLine("Installation failed, insufficient space! retrying with remove previous version");
                    _appSettings.TryOverwrite = false;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted);
                }

                _appSettings.TryOverwrite = false;
                Trace.WriteLine("Installation failed, insufficient space!");
                return InstallResult.FailureResult($"Installation failed: {Constants.LocalizationKeys.InsufficientSpace.Localized()}");
            }

            // Handle certificate mismatch: the installed copy was signed with a different
            // certificate, so Tizen refuses to overwrite it. The only fix is removing the old copy.
            if (installResults.Output.Contains(Constants.TizenErrorCodes.InstallFailed118012) ||
                installResults.Output.Contains(Constants.TizenErrorCodes.InstallFailed118Minus12))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                // On TVs that allow SDB uninstall, remove the old copy and reinstall automatically.
                if (_appSettings.TryOverwrite && await GetTvDiagnoseAsync(tvIpAddress))
                {
                    _appSettings.TryOverwrite = false;
                    _appSettings.ForceSamsungLogin = true;
                    _appSettings.DeletePreviousInstall = true;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted);
                }

                // Overwrite can't help and the TV can't remove it over USB -> tell the user to delete it manually.
                _appSettings.TryOverwrite = false;
                var certMessage = string.Format(
                    Constants.LocalizationKeys.CertificateMismatch.Localized(),
                    GetPackageAppTitle(packageUrl));
                progress?.Invoke(certMessage);
                return InstallResult.FailureResult(certMessage);
            }

            // Handle package ID conflict error. Note: a service-component incompatibility
            // (the common cause on older TVs) is already handled before install in Step 3b,
            // so reaching here generally means a genuine id/config conflict.
            if (installResults.Output.Contains(Constants.TizenErrorCodes.InstallFailed118))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    _appSettings.TryOverwrite = false;
                    await FileHelper.ModifyWgtPackageId(packageUrl);
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted);
                }

                _appSettings.TryOverwrite = false;
                return InstallResult.FailureResult($"Installation failed: {Constants.LocalizationKeys.ModifyConfigRequired.Localized()}");
            }

            // Handle generic failure
            if (installResults.Output.Contains(Constants.TizenErrorCodes.Failed))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

                if (_appSettings.TryOverwrite)
                {
                    _appSettings.TryOverwrite = false;
                    return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted);
                }

                _appSettings.TryOverwrite = false;
                return InstallResult.FailureResult($"Installation failed: {installResults.Output}");
            }

            // Handle success
            if (installResults.Output.Contains(Constants.TizenErrorCodes.Installing100) ||
                installResults.Output.Contains(Constants.TizenErrorCodes.InstallCompleted))
            {
                progress?.Invoke(Constants.LocalizationKeys.InstallationSuccessful.Localized());

                if (_appSettings.OpenAfterInstall)
                {
                    string tvAppId = await GetInstalledAppId(tvIpAddress, GetPackageAppTitle(packageUrl));
                    _ = Task.Run(async () =>
                    {
                        await _processHelper.RunCommandAsync(TizenSdbPath!, $"launch {tvIpAddress} \"{tvAppId}\"");
                    });
                }

                return InstallResult.SuccessResult();
            }

            // Unknown result - retry if possible
            progress?.Invoke(Constants.LocalizationKeys.InstallationFailed.Localized());

            if (_appSettings.TryOverwrite)
            {
                _appSettings.TryOverwrite = false;
                return await InstallPackageAsync(packageUrl, tvIpAddress, cancellationToken, progress, onSamsungLoginStarted);
            }

            _appSettings.TryOverwrite = false;
            return InstallResult.FailureResult($"Installation failed: {installResults.Output}");
        }

        #endregion

        #region TV Communication Methods

        public async Task<string> GetTvNameAsync(string tvIpAddress)
        {
            var output = await _processHelper.RunCommandAsync(TizenSdbPath!, $"devices {tvIpAddress}");
            return output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        }

        private async Task<(string tizenOs, string sdkToolPath)> FetchCapabilitiesAsync(string tvIpAddress)
        {
            var output = await _processHelper.RunCommandAsync(TizenSdbPath!, $"capability {tvIpAddress}");

            var versionMatch = RegexPatterns.TizenCapability.PlatformVersion.Match(output.Output);
            string tizenOs = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : string.Empty;

            var pathMatch = RegexPatterns.TizenCapability.SdkToolPath.Match(output.Output);
            string sdkToolPath = pathMatch.Success ? pathMatch.Groups[1].Value.Trim() : Constants.Defaults.SdkToolPath;

            return (tizenOs, sdkToolPath);
        }

        private async Task<string> GetTvDuidAsync(string tvIpAddress)
        {
            var output = await _processHelper.RunCommandAsync(TizenSdbPath!, $"duid {tvIpAddress}");
            return output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        }

        private async Task<bool> GetTvDiagnoseAsync(string tvIpAddress)
        {
            var output = await _processHelper.RunCommandAsync(TizenSdbPath!, $"diagnose {tvIpAddress}");
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
            var result = await _processHelper.RunCommandAsync(TizenSdbPath!, $"apps {tvIpAddress}");
            var output = result?.Output ?? string.Empty;

            // Read what the WGT *claims* its app id is (best effort fallback for "no listing" cases)
            var wgtAppId = await FileHelper.ReadWgtApplicationId(packageUrl);

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
            var output = await _processHelper.RunCommandAsync(TizenSdbPath!, $"apps {tvIpAddress}");
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
            return await _processHelper.RunCommandAsync(
                TizenSdbPath!,
                $"resign \"{packagePath}\" \"{authorP12}\" \"{distributorP12}\" {certPass}");
        }

        private async Task<ProcessResult> InstallPackageOnDeviceAsync(string tvIpAddress, string packagePath, string sdkToolPath)
        {
            return await _processHelper.RunCommandAsync(
                TizenSdbPath!,
                $"install {tvIpAddress} \"{packagePath}\" {sdkToolPath}");
        }

        private async Task<ProcessResult> UninstallPackageAsync(string tvIpAddress, string packageId)
        {
            return await _processHelper.RunCommandAsync(
                TizenSdbPath!,
                $"uninstall {tvIpAddress} {packageId}");
        }

        private async Task AllowPermitInstall(string tvIpAddress, string deviceXml, string sdkToolPath)
        {
            await _processHelper.RunCommandAsync(
                TizenSdbPath!,
                $"permit-install {tvIpAddress} \"{deviceXml}\" {sdkToolPath}");
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
