using Avalonia.Controls.ApplicationLifetimes;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
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
        public async Task<bool> InstallPackageAsync(string? packagePath, NetworkDevice selectedDevice, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null)
        {
            if(selectedDevice.DeveloperIP == null) return false;

            var localIps = _networkService.GetRelevantLocalIPs()
                              .Select(ip => ip.ToString())
                              .ToList();

            bool ipMismatch = !localIps.Contains(selectedDevice.DeveloperIP) && !string.IsNullOrEmpty(selectedDevice.DeveloperIP);

            if (!string.IsNullOrEmpty(AppSettings.Default.LocalIp)
                && !string.IsNullOrEmpty(selectedDevice.DeveloperIP)
                && _networkService.IsDifferentSubnet(AppSettings.Default.LocalIp, selectedDevice.DeveloperIP))
            {
                bool continueExecution =
                    await _dialogService.ShowConfirmationAsync(
                        "Subnet Mismatch",
                        "subnetMismatch".Localized(),
                        "keyContinue".Localized(),
                        "keyStop".Localized());

                if (!continueExecution)
                    return false;
            }

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

            if (selectedDevice.DeveloperMode == "0")
            {
                bool devmodeExecution = await _dialogService.ShowConfirmationAsync("Developer Disabled", "DeveloperModeRequired".Localized(), "keyContinue".Localized(), "keyStop".Localized());
                if (!devmodeExecution)
                    return false;
            }

            if (ipMismatch && AppSettings.Default.RTLReading)
            {
                ipMismatch = !localIps
                    .Select(ip => _networkService.InvertIPAddress(ip))
                    .Contains(selectedDevice.DeveloperIP);

                if (!ipMismatch)
                    selectedDevice.IpAddress = selectedDevice.DeveloperIP;
            }

            if (ipMismatch)
            {
                bool isReversedIp = localIps
                    .Select(ip => _networkService.InvertIPAddress(ip))
                    .Contains(selectedDevice.DeveloperIP);

                if (isReversedIp)
                {
                    bool continueExecution = await _dialogService.ShowConfirmationAsync(
                        "IP Reversed",
                        "DeveloperIPReversed".Localized(),
                        "keyContinue".Localized(),
                        "keyStop".Localized());
                    if (!continueExecution)
                        return false;
                    ipMismatch = false;
                }
            }

            if (ipMismatch)
            {
                bool continueExecution = await _dialogService.ShowConfirmationAsync("IP Mismatch", "DeveloperIPMismatch".Localized(), "keyContinue".Localized(), "keyStop".Localized());
                if (!continueExecution)
                    return false;
            }

            try
            {
                var result = await _tizenInstaller.InstallPackageAsync(
                    packagePath,
                    selectedDevice.IpAddress,
                    cancellationToken,
                    progress,
                    onSamsungLoginStarted);

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
        public async Task<bool> InstallCustomPackagesAsync(string[] packagePaths, NetworkDevice? device, CancellationToken cancellationToken, Action<string> onProgress, Action? onSamsungLoginStarted = null)
        {
            if (device == null) return false;

            onProgress("UsingCustomWGT".Localized());

            var allSuccessful = true;

            foreach (var packagePath in packagePaths)
            {
                var filePath = packagePath.Trim();
                if (!File.Exists(filePath))
                {
                    await _dialogService.ShowErrorAsync($"Package not found: {filePath}");
                    allSuccessful = false;
                    break;
                }

                var success = await InstallPackageAsync(filePath, device, cancellationToken);
                if (!success)
                {
                    allSuccessful = false;
                    break;
                }
            }

            return allSuccessful;
        }
        public void CleanupDownloadedPackage(string? downloadedPackagePath)
        {
            try
            {
                if (downloadedPackagePath != null && File.Exists(downloadedPackagePath))
                {
                    File.Delete(downloadedPackagePath);
                }
            }
            catch { /* Ignore cleanup errors */ }
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
