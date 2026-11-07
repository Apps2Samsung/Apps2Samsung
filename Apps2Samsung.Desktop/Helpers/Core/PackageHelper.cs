using Apps2Samsung.Helpers.Core;
using Apps2Samsung.TvStore;
using Apps2Samsung.Packaging;
using Apps2Samsung.Localization;
﻿using Avalonia.Controls.ApplicationLifetimes;
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

            // A signed repository that could not be checked now: listed, never installed from (#1018).
            if (Apps2Samsung.Catalog.Repositories.SignedRepositories.InstallBlocked(release, key => key.Localized()) is { } blocked)
            {
                await _dialogService.ShowErrorAsync(blocked);
                return null;
            }

            try
            {
                string downloadPath = await _tizenInstaller.DownloadPackageAsync(selectedAsset.DownloadUrl);
                // A repository's file installs only when its bytes are the ones it signed for (#1018).
                if (Apps2Samsung.Catalog.Repositories.SignedRepositories.DigestProblem(release, selectedAsset, downloadPath, key => key.Localized()) is { } digest)
                {
                    try { File.Delete(downloadPath); } catch (IOException) { /* best-effort */ }
                    progress?.Invoke("DownloadFailed".Localized());
                    await _dialogService.ShowErrorAsync(digest);
                    return null;
                }
                progress?.Invoke("DownloadCompleted".Localized());
                return downloadPath;
            }
            catch (Exception ex)
            {
                progress?.Invoke("DownloadFailed".Localized());
                // A connection problem gets a plain explanation; the exception goes to the log only.
                // Anything else keeps the full exception in the dialog, which reports rely on.
                if (Apps2Samsung.Services.NetworkFailure.LocalizationKey(ex) is { } key)
                {
                    Trace.WriteLine($"[Download] {selectedAsset.DownloadUrl}: {ex}");
                    await _dialogService.ShowErrorAsync($"{"DownloadFailed".Localized()} {key.Localized()}");
                }
                else
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

            ClearInstallFailure();

            // The device list is a scan snapshot; re-read the TV's Developer-Mode fields now so a
            // Developer-Mode IP corrected on the TV since the scan isn't still flagged as a mismatch.
            await TizenDeveloperInfo.RefreshAsync(_networkService, selectedDevice, cancellationToken);

            // Shared pre-install guards: Developer Mode off, a Developer-Mode IP pointing at another
            // machine (or typed back to front), a TV on another subnet, and a TV whose install service
            // isn't up yet. Same checks and wording as the mobile head, see Core InstallGuards.
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

            // The checks are about the developer connection; TV-Store's upload page needs none of it
            // (#979). A TV the checks would stop that runs TV-Store gets the offer to take the package
            // that way instead: TV-Store asks on the screen and signs it itself. Only when TV-Store is
            // already up: nothing is started on the TV uninvited (the toolbox's button does that).
            var offerTvIp = guardResult.CorrectedTvIp ?? selectedDevice.IpAddress;
            if (guardResult.Guards.Count > 0 && await TvStoreUpload.IsAvailableAsync(offerTvIp, cancellationToken))
            {
                var detail = string.Join("\n", guardResult.Guards.Select(g => g.TitleKey.Localized()));
                if (await _dialogService.ShowConfirmationAsync(
                        "tvStoreOfferTitle".Localized(),
                        $"{"tvStoreOfferBody".Localized()}\n\n{detail}",
                        "keySendToTvStore".Localized(),
                        "keyUseDeveloperConnection".Localized()))
                {
                    return await SendToTvStoreAsync(packagePath, offerTvIp, progress, cancellationToken);
                }
            }

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

            // Updating TV-Store through TV-Store's own relay (#1005): say once, before the install,
            // that the TV will drop this connection while it restarts TV-Store and that the update
            // still goes through, so the drop is not read as a failed install.
            if (await TvStoreSelfUpdate.AppliesAsync(selectedDevice.IpAddress, packagePath, AppSettings.Default.PackageIdRenamesJson, cancellationToken))
            {
                progress?.Invoke(TvStoreSelfUpdate.NoticeKey.Localized());
                await _dialogService.ShowMessageAsync(TvStoreSelfUpdate.NoticeTitleKey.Localized(), TvStoreSelfUpdate.NoticeKey.Localized());
            }

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
                    await NoteInstallFailureAsync(selectedDevice, result.ErrorMessage ?? "InstallationFailed".Localized());
                    await _dialogService.ShowErrorAsync($"{"InstallationFailed".Localized()}: {result.ErrorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                progress?.Invoke("InstallationFailed".Localized());
                await NoteInstallFailureAsync(selectedDevice, ex.Message);
                await _dialogService.ShowErrorAsync($"{"InstallationFailed".Localized()}: {ex}");
                return false;
            }
        }

        /// <summary>
        /// The way in without the developer connection (#979): the package, customized as an install
        /// would (the values asked before install included) but not signed, goes to TV-Store's upload
        /// page on the TV. TV-Store comes to the front and asks there; the install happens when
        /// Install is pressed on the TV, so true here means the file waits on the TV.
        /// </summary>
        private async Task<bool> SendToTvStoreAsync(string packagePath, string tvIp, ProgressCallback? progress, CancellationToken cancellationToken)
        {
            Action<string> report = line =>
            {
                var text = ProgressText.Localize(line, k => k.Localized());
                progress?.Invoke(text);
            };
            try
            {
                // A copy: the customization edits the file in place, never the download or the user's file.
                using var staged = StagedPackage.Create(packagePath);
                await _tizenInstaller.CustomizeAsync(tvIp, staged.FilePath);
                var sent = await TvStoreHandoff.SendAsync(tvIp, staged.FilePath, report, cancellationToken);
                var body = string.Format((sent.TvStoreOpened ? "tvStoreSentBody" : "tvStoreSentBodyClosed").Localized(), sent.Upload.Name);
                progress?.Invoke(body);
                await _dialogService.ShowMessageAsync("tvStoreSentTitle".Localized(), body);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvStore] hand-off failed: {ex}");
                progress?.Invoke("InstallationFailed".Localized());
                await _dialogService.ShowErrorAsync($"{"InstallationFailed".Localized()}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// What the last install on the selected TV failed with; null after a success, a cancel, or
        /// once the TV or the list changed. The TV card turns amber on it even when the TV reads as
        /// ready, so a refused connection does not leave a green card behind a closed dialog.
        /// </summary>
        public string? LastInstallFailure { get; private set; }

        /// <summary>Raised on any thread when <see cref="LastInstallFailure"/> changes.</summary>
        public event Action? InstallFailureChanged;

        public void ClearInstallFailure()
        {
            if (LastInstallFailure is null)
                return;
            LastInstallFailure = null;
            InstallFailureChanged?.Invoke();
        }

        // Re-read the TV first: a Host PC IP changed since the scan, or a relay just switched off,
        // is then what the card says, instead of the stale scan verdict.
        private async Task NoteInstallFailureAsync(NetworkDevice device, string message)
        {
            try { await TizenDeveloperInfo.RefreshAsync(_networkService, device); }
            catch (Exception ex) { Trace.WriteLine($"[install] Could not re-read the TV after the failure: {ex.Message}"); }
            LastInstallFailure = message;
            InstallFailureChanged?.Invoke();
        }

        /// <summary>Re-reads the TV's Developer-Mode fields, debug port and relay port (the card's Check again).</summary>
        public Task RefreshDeviceAsync(NetworkDevice device, CancellationToken cancellationToken = default)
            => TizenDeveloperInfo.RefreshAsync(_networkService, device, cancellationToken);
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
