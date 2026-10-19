using Avalonia.Controls.ApplicationLifetimes;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class PackageHelper(
        ITizenInstallerService tizenInstaller,
        IDialogService dialogService,
        INetworkService networkService)
    {
        private readonly ITizenInstallerService _tizenInstaller = tizenInstaller;
        private readonly IDialogService _dialogService = dialogService;
        private readonly INetworkService _networkService = networkService;

        public async Task<string?> DownloadReleaseAsync(GitHubRelease release, Asset? selectedAsset, ProgressCallback? progress = null)
        {
            if (release?.PrimaryDownloadUrl == null) return null;
            if (selectedAsset?.DownloadUrl == null) return null;

            try
            {
                string downloadPath = await _tizenInstaller.DownloadPackageAsync(selectedAsset.DownloadUrl);
                progress?.Invoke("DownloadCompleted".Localized());
                return downloadPath;
            }
            catch (Exception ex)
            {
                progress?.Invoke("DownloadFailed".Localized());
                await _dialogService.ShowErrorAsync($"{"DownloadFailed".Localized()} {ex}");
                return null;
            }
        }
        public async Task<bool> InstallPackageAsync(string? packagePath, NetworkDevice selectedDevice, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null, IProgress<int>? installProgress = null)
        {
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
            {
                progress?.Invoke("NoPackageToInstall".Localized());
                await _dialogService.ShowErrorAsync("NoPackageToInstall".Localized());
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedDevice?.IpAddress))
            {
                progress?.Invoke("NoDeviceSelected".Localized());
                await _dialogService.ShowErrorAsync("NoDeviceSelected".Localized());
                return false;
            }

            // The device list is a scan snapshot; re-read the TV's Developer-Mode fields now so a
            // Developer-Mode IP corrected on the TV since the scan isn't still flagged as a mismatch.
            await TizenDeveloperInfo.RefreshAsync(_networkService, selectedDevice, cancellationToken);

            // Shared pre-install guards: Developer Mode off, a Developer-Mode IP pointing at another
            // machine (or typed back to front), a TV on another subnet, and a TV whose install service
            // isn't up yet. Same checks and wording as the mobile head — see Core InstallGuards.
            var localIps = LocalIps();
            var guardResult = InstallGuards.Evaluate(
                selectedDevice,
                new InstallGuardOptions
                {
                    LocalIps = localIps,
                    ConfiguredLocalIp = AppSettings.Default.LocalIp,
                    ReversedIpReading = AppSettings.Default.RTLReading,
                },
                _networkService);

            // The Developer-Mode guards share one answer: the guide, which re-checks the TV while it
            // is open and turns into Continue once it is fixed. The rest stay a Continue/Stop question.
            var developerModeGuards = guardResult.Guards.Where(g => g.Kind is InstallGuardKind.RestartRequired
                or InstallGuardKind.DeveloperModeOff or InstallGuardKind.DeveloperIpReversed or InstallGuardKind.DeveloperIpMismatch).ToList();
            if (developerModeGuards.Count > 0 &&
                !await ShowDevModeGuideAsync(selectedDevice, localIps, fromInstall: true, cancellationToken))
                return false;

            foreach (var guard in guardResult.Guards.Except(developerModeGuards))
            {
                // Detail is measured facts (the IPs involved), not translatable prose.
                var message = string.IsNullOrEmpty(guard.Detail)
                    ? guard.MessageKey.Localized()
                    : $"{guard.MessageKey.Localized()}\n\n{guard.Detail}";

                bool continueExecution = await _dialogService.ShowConfirmationAsync(
                    guard.TitleKey.Localized(),
                    message,
                    "keyContinue".Localized(),
                    "keyStop".Localized());

                if (!continueExecution)
                    return false;
            }

            // The TV's Developer-Mode IP read back to front matches ours and the user reads IPs
            // right-to-left, so that reversed address is the one to install to.
            if (guardResult.CorrectedTvIp is not null)
                selectedDevice.IpAddress = guardResult.CorrectedTvIp;

            try
            {
                var result = await _tizenInstaller.InstallPackageAsync(
                    packagePath,
                    selectedDevice.IpAddress,
                    cancellationToken,
                    progress,
                    onSamsungLoginStarted,
                    installProgress: installProgress);

                if (result.Success)
                {
                    var win = App.Services.GetRequiredService<InstallationCompleteWindow>();

                    var prettyName = GetPrettyPackageName(packagePath);

                    if (win.DataContext is InstallationCompleteViewModel vm)
                    {
                        vm.InstalledPackageName = prettyName;
                    }

                    if (Avalonia.Application.Current?.ApplicationLifetime is
                        IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        await win.ShowDialog(desktop.MainWindow);
                    }

                    return true;
                }
                else if (result.AlreadyShown)
                {
                    // A sheet or dialog already said what happened (or the user cancelled).
                    progress?.Invoke(result.ErrorMessage);
                    return false;
                }
                else
                {
                    progress?.Invoke("InstallationFailed".Localized());
                    await _dialogService.ShowErrorAsync($"{"InstallationFailed".Localized()}: {result.ErrorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                progress?.Invoke("InstallationFailed".Localized());
                await _dialogService.ShowErrorAsync($"{"InstallationFailed".Localized()}: {ex}");
                return false;
            }
        }
        /// <summary>
        /// The Developer Mode guide for <paramref name="device"/>. From Install it returns whether to
        /// go on; from the TV card it is just a guide.
        /// </summary>
        public Task<bool> ShowDevModeGuideAsync(NetworkDevice device, IReadOnlyCollection<string>? localIps = null, bool fromInstall = false, CancellationToken cancellationToken = default)
        {
            var name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.IpAddress : device.DeviceName!;
            localIps ??= LocalIps();
            // The address to type on the TV: the network card picked in Settings, else the first one.
            var localIp = string.IsNullOrEmpty(AppSettings.Default.LocalIp) ? localIps.FirstOrDefault() : AppSettings.Default.LocalIp;
            return _dialogService.ShowDevModeGuideAsync(
                device,
                name,
                () => TizenDeveloperInfo.RefreshAsync(_networkService, device, cancellationToken),
                localIps,
                localIp,
                fromInstall);
        }

        // GetRelevantLocalIPs folds the hand-typed TV address (UserCustomIP) into the list for the
        // scan's benefit; it is not one of this machine's IPs, so keep it out of the "this device"
        // side of the comparison.
        public List<string> LocalIps() => _networkService.GetRelevantLocalIPs()
            .Select(ip => ip.ToString())
            .Where(ip => ip != AppSettings.Default.UserCustomIP)
            .ToList();

        public async Task<bool> InstallCustomPackagesAsync(string[] packagePaths, NetworkDevice? device, CancellationToken cancellationToken, Action<string> onProgress, Action? onSamsungLoginStarted = null, IProgress<int>? installProgress = null)
        {
            if (device == null) return false;

            onProgress("UsingCustomWGT".Localized());

            var allSuccessful = true;

            foreach (var packagePath in packagePaths)
            {
                var filePath = packagePath.Trim();
                if (!File.Exists(filePath))
                {
                    await _dialogService.ShowErrorAsync(string.Format("statusPackageNotFound".Localized(), filePath));
                    allSuccessful = false;
                    break;
                }

                var success = await InstallPackageAsync(filePath, device, cancellationToken, installProgress: installProgress);
                if (!success)
                {
                    allSuccessful = false;
                    break;
                }
            }

            return allSuccessful;
        }
        // Deletes a package this app downloaded. Only that: the custom-package path hands the user's
        // own .wgt to the same cleanup, and eating a file someone picked from their own disk is not
        // what "Preserve WGT file" is offering to keep. Anything outside the download cache is left
        // exactly where it was found.
        public void CleanupDownloadedPackage(string? downloadedPackagePath)
        {
            try
            {
                if (downloadedPackagePath == null || !File.Exists(downloadedPackagePath))
                    return;

                if (!IsInDownloadCache(downloadedPackagePath))
                {
                    Trace.WriteLine($"[Cleanup] Keeping {downloadedPackagePath}: not a package this app downloaded.");
                    return;
                }

                File.Delete(downloadedPackagePath);
            }
            catch { /* Ignore cleanup errors */ }
        }

        private static bool IsInDownloadCache(string path)
        {
            try
            {
                var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppSettings.DownloadPath));
                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""));

                // Case-insensitive on Windows and macOS, case-sensitive on Linux, same as the file system.
                var comparison = OperatingSystem.IsLinux()
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                return string.Equals(cache, folder, comparison);
            }
            catch
            {
                return false;
            }
        }
        private static string GetPrettyPackageName(string packagePath)
        {
            var name = Path.GetFileNameWithoutExtension(packagePath);

            if (string.IsNullOrEmpty(name))
                return string.Empty;

            return char.ToUpper(name[0]) + name.Substring(1);
        }

    }
}
