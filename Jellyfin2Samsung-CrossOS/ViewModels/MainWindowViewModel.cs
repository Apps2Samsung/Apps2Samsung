using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Tizen.Devices;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly ITizenInstallerService _tizenInstaller;
        private readonly IDialogService _dialogService;
        private readonly INetworkService _networkService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService _themeService;
        private readonly IUpdaterService _updaterService;
        private readonly IUpdateDialogService _updateDialogService;
        private readonly FileHelper _fileHelper;
        private readonly DeviceHelper _deviceHelper;
        private readonly TizenApiClient _tizenApiClient;
        private readonly PackageHelper _packageHelper;
        private readonly SettingsWindowViewModel _settingsViewModel;
        private readonly AddLatestRelease _addLatestRelease;
        private readonly ProviderManifestService _providerManifestService;
        private CancellationTokenSource? _samsungLoginCts;
        private CancellationTokenSource? _initializationCts;

        [ObservableProperty]
        private ObservableCollection<GitHubRelease> releases = new ObservableCollection<GitHubRelease>();

        [ObservableProperty]
        private ObservableCollection<Asset> availableAssets = new ObservableCollection<Asset>();

        [ObservableProperty]
        private ObservableCollection<NetworkDevice> availableDevices = new ObservableCollection<NetworkDevice>();

        [ObservableProperty]
        private GitHubRelease? selectedRelease;

        [ObservableProperty]
        private Asset? selectedAsset;

        [ObservableProperty]
        private string customWgtPath = string.Empty;

        [ObservableProperty]
        private NetworkDevice? selectedDevice;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool isLoadingDevices;

        [ObservableProperty]
        private string statusBar = string.Empty;

        [ObservableProperty]
        private bool isSamsungLoginActive;

        [ObservableProperty]
        private bool darkMode;

        private string _currentStatusKey = string.Empty;

        private string? _downloadedPackagePath;
        private string L(string key) => _localizationService.GetString(key);

        public bool EnableDevicesInput => !IsLoadingDevices;
        public string LblRelease => _localizationService.GetString("lblRelease");
        public string LblVersion => _localizationService.GetString("lblVersion");
        public string LblSelectTv => _localizationService.GetString("lblSelectTv");
        public string DownloadAndInstall => _localizationService.GetString("DownloadAndInstall");
        public string lblCustomWgt => _localizationService.GetString("lblCustomWgt");
        public string SelectWGT => _localizationService.GetString("SelectWGT");
        public static string FooterText =>
            $"{AppSettings.Default.AppVersion} " +
            $"- Copyright (c) {DateTime.Now.Year} - MIT License - Patrick Stel";

        public MainWindowViewModel(
            ITizenInstallerService tizenInstaller,
            IDialogService dialogService,
            INetworkService networkService,
            ILocalizationService localizationService,
            IThemeService themeService,
            IUpdaterService updaterService,
            IUpdateDialogService updateDialogService,
            HttpClient httpClient,
            DeviceHelper deviceHelper,
            TizenApiClient tizenApiClient,
            PackageHelper packageHelper,
            FileHelper fileHelper,
            SettingsWindowViewModel settingsViewModel
        )
        {
            _tizenInstaller = tizenInstaller;
            _dialogService = dialogService;
            _networkService = networkService;
            _deviceHelper = deviceHelper;
            _tizenApiClient = tizenApiClient;
            _packageHelper = packageHelper;
            _localizationService = localizationService;
            _themeService = themeService;
            _updaterService = updaterService;
            _updateDialogService = updateDialogService;
            _fileHelper = fileHelper;
            _settingsViewModel = settingsViewModel;

            _addLatestRelease = new AddLatestRelease(httpClient);
            _providerManifestService = new ProviderManifestService(httpClient);

            _localizationService.LanguageChanged += OnLanguageChanged;
            _themeService.ThemeChanged += OnThemeChanged;

            // Initialize dark mode state from settings
            DarkMode = AppSettings.Default.DarkMode;
        }


        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshLocalizedProperties();

            if (!string.IsNullOrEmpty(_currentStatusKey))
                StatusBar = L(_currentStatusKey);
        }

        private void OnThemeChanged(object? sender, bool isDarkMode)
        {
            DarkMode = isDarkMode;
        }

        partial void OnDarkModeChanged(bool value)
        {
            _themeService.SetTheme(value);
        }
        private void SetStatus(string key)
        {
            _currentStatusKey = key;
            StatusBar = L(key);
        }

        private void RefreshLocalizedProperties()
        {
            OnPropertyChanged(nameof(LblRelease));
            OnPropertyChanged(nameof(LblVersion));
            OnPropertyChanged(nameof(LblSelectTv));
            OnPropertyChanged(nameof(DownloadAndInstall));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(StatusBar));
            OnPropertyChanged(nameof(lblCustomWgt));
            OnPropertyChanged(nameof(SelectWGT));
        }

        partial void OnSelectedReleaseChanged(GitHubRelease? value)
        {
            AvailableAssets = value != null
                ? new ObservableCollection<Asset>(value.Assets)
                : new ObservableCollection<Asset>();

            SelectedAsset =
                AvailableAssets.FirstOrDefault(a => a.IsDefault)
                ?? AvailableAssets.FirstOrDefault();

            RefreshCanExecuteChanged();
        }

        partial void OnSelectedAssetChanged(Asset? value)
        {
            RefreshCanExecuteChanged();
        }
        partial void OnCustomWgtPathChanged(string value)
        {
            AppSettings.Default.CustomWgtPath = value;
            AppSettings.Default.Save();

            DownloadAndInstallCommand?.NotifyCanExecuteChanged();
            DownloadCommand?.NotifyCanExecuteChanged();
        }

        partial void OnSelectedDeviceChanged(NetworkDevice? value)
        {
            if (value?.IpAddress == L("lblOther"))
                _ = PromptForManualIpAsync();

            RefreshCanExecuteChanged();
            AppSettings.Default.TvIp = value?.IpAddress ?? string.Empty;
            AppSettings.Default.Save();
        }

        partial void OnIsLoadingChanged(bool value)
        {
            RefreshCanExecuteChanged();
        }

        partial void OnIsLoadingDevicesChanged(bool value)
        {
            OnPropertyChanged(nameof(EnableDevicesInput));
            RefreshCanExecuteChanged();
        }

        private void RefreshCanExecuteChanged()
        {
            RefreshCommand.NotifyCanExecuteChanged();
            RefreshDevicesCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            DownloadAndInstallCommand.NotifyCanExecuteChanged();
            OpenSettingsCommand.NotifyCanExecuteChanged();
            CancelSamsungLoginCommand.NotifyCanExecuteChanged();
        }

        public async Task InitializeAsync()
        {
            // Create a new CTS for initialization that can be cancelled when update dialog shows
            _initializationCts?.Cancel();
            _initializationCts?.Dispose();
            _initializationCts = new CancellationTokenSource();
            var token = _initializationCts.Token;

            try
            {
                // Check for updates first (non-blocking, runs in background)
                _ = CheckForUpdatesAsync();

                SetStatus("CheckingTizenSdb");

                string tizenSdb = await _tizenInstaller.EnsureTizenSdbAvailable();

                if (string.IsNullOrEmpty(tizenSdb))
                {
                    SetStatus("FailedTizenSdb");
                    return;
                }

                ProcessHelper.KillSdbServers();

                await LoadReleasesAsync(token);
                token.ThrowIfCancellationRequested();

                SetStatus("ScanningNetwork");
                await LoadDevicesAsync(token);
                CustomWgtPath = AppSettings.Default.CustomWgtPath ?? "";
            }
            catch (OperationCanceledException)
            {
                // Initialization was cancelled (likely due to update dialog)
                Trace.WriteLine("Initialization cancelled");
            }
            catch (Exception ex)
            {
                SetStatus("InitializationFailed");
                await _dialogService.ShowErrorAsync($"{L("InitializationFailed")} {ex}");
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            // Skip if update check is disabled
            if (!AppSettings.Default.CheckForUpdatesOnStartup)
                return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Constants.Updater.UpdateCheckTimeoutSeconds));
                var updateResult = await _updaterService.CheckForUpdateAsync(cts.Token);

                if (!updateResult.IsSuccess || !updateResult.IsUpdateAvailable)
                    return;

                // Skip if user previously skipped this version
                if (updateResult.LatestVersion == AppSettings.Default.SkippedUpdateVersion)
                    return;

                // Cancel initialization tasks (network scan, release loading) before showing dialog
                // This prevents the UI from freezing while background tasks continue
                _initializationCts?.Cancel();

                // Show update dialog on UI thread
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var choice = await _updateDialogService.ShowUpdateAvailableDialogAsync(updateResult);

                    switch (choice)
                    {
                        case UpdateDialogChoice.Manual:
                            _updaterService.OpenReleasesPage();
                            // Resume initialization after user sees the releases page
                            _ = ResumeInitializationAsync();
                            break;

                        case UpdateDialogChoice.Automatic:
                            await PerformAutomaticUpdateAsync(updateResult);
                            // No resume needed - app will restart
                            break;

                        case UpdateDialogChoice.Skip:
                            AppSettings.Default.SkippedUpdateVersion = updateResult.LatestVersion;
                            AppSettings.Default.Save();
                            // Resume initialization after user skips
                            _ = ResumeInitializationAsync();
                            break;

                        case UpdateDialogChoice.Cancel:
                        default:
                            // Resume initialization after user cancels
                            _ = ResumeInitializationAsync();
                            break;
                    }
                });

                // Update last check time
                AppSettings.Default.LastUpdateCheck = DateTime.UtcNow;
                AppSettings.Default.Save();
            }
            catch (OperationCanceledException)
            {
                // Timeout - silently ignore
                Trace.WriteLine("Update check timed out");
            }
            catch (Exception ex)
            {
                // Don't show errors for update check failures - it's not critical
                Trace.WriteLine($"Update check failed: {ex}");
            }
        }

        private async Task PerformAutomaticUpdateAsync(Models.UpdateCheckResult updateResult)
        {
            // Installer/package-manager managed installs must update via their own channel.
            if (!updateResult.SupportsAutomaticUpdate || !_updaterService.IsAutomaticUpdateSupported())
            {
                _updaterService.OpenReleasesPage();
                return;
            }

            if (string.IsNullOrEmpty(updateResult.DownloadUrl))
            {
                await _updateDialogService.ShowUpdateErrorAsync(L("UpdateError"));
                return;
            }

            try
            {
                // Download update
                var progress = new Progress<int>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusBar = $"{L("UpdateDownloading")} {p}%";
                    });
                });

                StatusBar = L("UpdateDownloading");
                var downloadedPath = await _updaterService.DownloadUpdateAsync(updateResult.DownloadUrl, progress);

                // Apply update
                await _updateDialogService.ShowApplyingUpdateMessageAsync();
                var success = await _updaterService.ApplyUpdateAsync(downloadedPath);

                if (success)
                {
                    // Exit application - update script will restart it
                    if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Automatic update failed: {ex}");
                await _updateDialogService.ShowUpdateErrorAsync(ex.Message);
            }
        }

        private async Task ResumeInitializationAsync()
        {
            // Create a new CTS for the resumed initialization
            _initializationCts?.Dispose();
            _initializationCts = new CancellationTokenSource();
            var token = _initializationCts.Token;

            try
            {
                // Only load releases and devices if they haven't been loaded yet.
                // Note: Releases always contains the "Custom WGT File" entry, so we
                // check for *real* provider releases, not just any entry.
                if (!HasRealReleases)
                {
                    await LoadReleasesAsync(token);
                    token.ThrowIfCancellationRequested();
                }

                if (!AvailableDevices.Any(d => d.IpAddress != L("lblOther")))
                {
                    SetStatus("ScanningNetwork");
                    await LoadDevicesAsync(token);
                }

                CustomWgtPath = AppSettings.Default.CustomWgtPath ?? "";
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine("Resumed initialization cancelled");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Resumed initialization failed: {ex}");
            }
        }

        [RelayCommand(CanExecute = nameof(CanRefresh))]
        private async Task RefreshAsync()
        {
            await LoadReleasesAsync();
        }

        [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
        private async Task RefreshDevicesAsync()
        {
            await LoadDevicesAsync();
        }

        [RelayCommand(CanExecute = nameof(CanDownload))]
        private async Task DownloadAsync()
        {
            if (SelectedRelease != null)
            {
                await _packageHelper.DownloadReleaseAsync(
                    SelectedRelease,
                    SelectedAsset);
            }
        }

        [RelayCommand(CanExecute = nameof(CanInstall))]
        private async Task InstallAsync()
        {
            _samsungLoginCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            if (SelectedDevice != null)
            {
                await _packageHelper.InstallPackageAsync(
                    _downloadedPackagePath,
                    SelectedDevice,
                    _samsungLoginCts.Token);
            }
        }

        [RelayCommand(CanExecute = nameof(CanDownload))]
        private async Task DownloadAndInstallAsync()
        {
            _samsungLoginCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            if ((SelectedRelease != null && SelectedDevice != null) || (!string.IsNullOrEmpty(AppSettings.Default.CustomWgtPath)))
            {
                var customPaths = AppSettings.Default.CustomWgtPath?.Split(';', StringSplitOptions.RemoveEmptyEntries);

                if (customPaths?.Length > 0)
                {
                    try
                    {
                        await _packageHelper.InstallCustomPackagesAsync(
                            customPaths,
                            SelectedDevice,
                            _samsungLoginCts.Token,
                            progress => Dispatcher.UIThread.Post(() => StatusBar = progress),
                            onSamsungLoginStarted: OnSamsungLoginStarted);
                    }
                    finally
                    {
                        IsSamsungLoginActive = false;
                        _samsungLoginCts.Dispose();
                        _samsungLoginCts = null;
                    }

                    foreach (var customPath in customPaths)
                        if (!AppSettings.Default.KeepWGTFile)
                            _packageHelper.CleanupDownloadedPackage(customPath);

                    CustomWgtPath = string.Empty;
                }
                else
                {

                    if(SelectedRelease == null || SelectedDevice == null)
                        return;

                    string? downloadPath = await _packageHelper.DownloadReleaseAsync(
                        SelectedRelease,
                        SelectedAsset,
                        message => Dispatcher.UIThread.Post(() => StatusBar = message));

                    if (!string.IsNullOrEmpty(downloadPath))
                    {
                        try
                        {
                            await _packageHelper.InstallPackageAsync(
                                downloadPath,
                                SelectedDevice,
                                _samsungLoginCts.Token,
                                message => Dispatcher.UIThread.Post(() => StatusBar = message),
                                onSamsungLoginStarted: OnSamsungLoginStarted);
                        }
                        finally
                        {
                            IsSamsungLoginActive = false;
                            _samsungLoginCts.Dispose();
                            _samsungLoginCts = null;
                        }

                        if (!AppSettings.Default.KeepWGTFile)
                            _packageHelper.CleanupDownloadedPackage(downloadPath);
                    }
                }
            }
        }

        [RelayCommand(CanExecute = nameof(CanOpenSettings))]
        private async Task OpenSettings()
        {
            var showAllBefore = AppSettings.Default.ShowAllJellyfinVersions;
            var settingsWindow = new Apps2Samsung.Views.SettingsWindow(_settingsViewModel);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                await settingsWindow.ShowDialog(desktop.MainWindow);
            }

            // Reflect a changed version-list preference without an app restart.
            if (AppSettings.Default.ShowAllJellyfinVersions != showAllBefore)
                await LoadReleasesAsync();
        }
        [RelayCommand]
        private async Task ShowBuildInfoAsync()
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return;

                var buildInfoWindow = new Views.BuildInfoWindow();

                // Show as modal dialog centered on MainWindow
                await buildInfoWindow.ShowDialog(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync($"Failed to open build info window: {ex}");
            }
        }

        [RelayCommand]
        private async Task BrowseWgtAsync()
        {
            var mainWindow = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow : null;

            if (mainWindow?.StorageProvider != null)
            {
                var result = await _fileHelper.BrowseWgtFilesAsync(mainWindow.StorageProvider);
                if (!string.IsNullOrEmpty(result))
                    CustomWgtPath = result;
            }
        }
        [RelayCommand(CanExecute = nameof(IsSamsungLoginActive))]
        private void CancelSamsungLogin()
        {
            _samsungLoginCts?.Cancel();
        }

        private bool CanRefresh() => !IsLoading;
        private bool CanRefreshDevices() => !IsLoadingDevices;
        private bool CanOpenSettings() => !IsLoadingDevices;

        private bool CanDownload()
        {
            if (!string.IsNullOrEmpty(AppSettings.Default.CustomWgtPath))
            {
                var files = AppSettings.Default.CustomWgtPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                return files.All(File.Exists) &&
                       !IsLoading &&
                       SelectedDevice != null &&
                       !string.IsNullOrWhiteSpace(SelectedDevice.IpAddress);
            }

            return !IsLoading &&
                   SelectedRelease != null &&
                   SelectedAsset != null &&
                   SelectedDevice != null &&
                   !string.IsNullOrWhiteSpace(SelectedDevice.IpAddress);
        }


        private bool CanInstall()
        {
            // If custom wgt path is set, allow install without _downloadedPackagePath
            if (!string.IsNullOrEmpty(AppSettings.Default.CustomWgtPath))
            {
                var files = AppSettings.Default.CustomWgtPath.Split(';');
                return files.All(File.Exists) &&
                       SelectedDevice != null &&
                       !string.IsNullOrWhiteSpace(SelectedDevice.IpAddress);
            }

            // Otherwise fallback to _downloadedPackagePath logic
            return !string.IsNullOrEmpty(_downloadedPackagePath) &&
                   File.Exists(_downloadedPackagePath) &&
                   SelectedDevice != null &&
                   !string.IsNullOrWhiteSpace(SelectedDevice.IpAddress);
        }


        // True once the release list holds something other than the always-present
        // "Custom WGT File" entry — i.e. providers actually loaded.
        private bool HasRealReleases =>
            Releases.Any(r => r.Name != Constants.AppIdentifiers.CustomWgtFile);

        /// <summary>
        /// Safety net: reload the release list if it never populated (e.g. the
        /// initial load was cancelled by the update check or a re-init race).
        /// Runs uncancellable so a pending init cancel can't kill it again.
        /// </summary>
        public async Task EnsureReleasesLoadedAsync()
        {
            if (IsLoading || HasRealReleases)
                return;

            await LoadReleasesAsync();
        }

        private async Task LoadReleasesAsync(CancellationToken cancellationToken = default)
        {
            // Guard against concurrent loads (init vs. the recovery/refresh paths).
            if (IsLoading)
                return;

            IsLoading = true;
            try
            {
                var list = new List<GitHubRelease>();
                var manifest = await _providerManifestService.GetAsync();
                foreach (var provider in manifest.Providers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(provider.Url))
                        continue;

                    // Multi-version providers (Jellyfin history) collapse to just
                    // the latest release unless the user opted into the full list.
                    var take = provider.Take;
                    if (take > 1 && !AppSettings.Default.ShowAllJellyfinVersions)
                        take = 1;

                    var release = await _addLatestRelease.GetReleasesAsync(
                        provider.Url,
                        provider.Prefix,
                        provider.DisplayName,
                        take);
                    if (release.Count == 0)
                        continue;

                    if (provider.ExpandAssets)
                    {
                        // One entry per .wgt so community apps show up as
                        // first-class items instead of a single bundle entry.
                        foreach (var r in release)
                            foreach (var asset in r.Assets)
                                list.Add(new GitHubRelease
                                {
                                    Name = Path.GetFileNameWithoutExtension(asset.FileName),
                                    TagName = r.TagName,
                                    PublishedAt = r.PublishedAt,
                                    Url = r.Url,
                                    Assets = new List<Asset> { asset }
                                });
                    }
                    else
                    {
                        list.AddRange(release);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Show every app alphabetically (A-Z, 0-9) regardless of manifest/fetch order.
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                Releases.Clear();
                foreach (var r in list)
                    Releases.Add(r);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                await _dialogService.ShowErrorAsync($"{L(Constants.LocalizationKeys.FailedLoadingReleases)} {ex}");
            }
            finally
            {
                // Always add the custom WGT option, regardless of GitHub failures
                if (!Releases.Any(r => r.Name == Constants.AppIdentifiers.CustomWgtFile))
                {
                    Releases.Add(new GitHubRelease
                    {
                        Name = Constants.AppIdentifiers.CustomWgtFile,
                        TagName = string.Empty,
                        PublishedAt = string.Empty,
                        Url = string.Empty,
                        Assets = new List<Asset>()
                    });
                }
                IsLoading = false;
            }
        }

        private async Task LoadDevicesAsync(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            IsLoadingDevices = true;
            AvailableDevices.Clear();

            try
            {
                string? selectedIp = SelectedDevice?.IpAddress;

                var devices = await _deviceHelper.ScanForDevicesAsync(cancellationToken, virtualScan);
                foreach (var device in devices)
                    AvailableDevices.Add(device);

                if (AvailableDevices.Count == 0)
                {
                    if (!virtualScan)
                    {
                        SetStatus("NoDevicesFoundRetry");
                        var rescan = await _dialogService.ShowConfirmationAsync(
                            L("NoDevicesFound"),
                            L("RetySearchMsg"),
                            L("keyYes"),
                            L("keyNo"));

                        if (rescan)
                            await LoadDevicesAsync(cancellationToken, true);
                        else
                        {
                            SetStatus("NoDevicesFound");
                            return;
                        }
                    }
                    else
                    {
                        SetStatus("NoDevicesFound");
                    }
                }
                else
                {
                    SetStatus("Ready");
                }

                if (AvailableDevices.Any())
                {
                    if (SelectedDevice == null)
                        SelectedDevice = AvailableDevices[0];
                    else if (!string.IsNullOrEmpty(selectedIp))
                    {
                        var previousDevice = AvailableDevices.FirstOrDefault(it => it.IpAddress == selectedIp);
                        if (previousDevice != null)
                            SelectedDevice = previousDevice;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Re-throw to be handled by caller
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync($"Failed to load devices: {ex}");
            }
            finally
            {
                if (!AvailableDevices.Any(d => d.IpAddress == L("lblOther")))
                {
                    AvailableDevices.Add(new NetworkDevice
                    {
                        IpAddress = L("lblOther"),
                        Manufacturer = null,
                        DeviceName = L("IpNotListed")
                    });
                }

                IsLoadingDevices = false;
            }
        }
        private async Task PromptForManualIpAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;

            var dialog = new IpInputDialog();


            string? ip = await dialog.ShowDialogAsync(desktop.MainWindow);

            if (string.IsNullOrWhiteSpace(ip))
            {
                SelectedDevice = AvailableDevices.FirstOrDefault(d => d.IpAddress != "Other");
                return;
            }

            var device = await _networkService.ValidateManualTizenAddress(ip);
            if (device == null)
            {
                SelectedDevice = AvailableDevices.FirstOrDefault(d => d.IpAddress != "Other");
                await _dialogService.ShowErrorAsync(L("InvalidDeviceIp"));
                return;
            }

            var samsungDevice = await _tizenApiClient.GetDeveloperInfoAsync(device);

            if (samsungDevice != null)
            {
                AppSettings.Default.UserCustomIP = samsungDevice.IpAddress;
                AppSettings.Default.Save();

                SelectedDevice = samsungDevice;

                if (!AvailableDevices.Any(d => d.IpAddress == device.IpAddress))
                    AvailableDevices.Add(samsungDevice);
            }
            else
            {
                SelectedDevice = AvailableDevices.FirstOrDefault(d => d.IpAddress != "Other");
                await _dialogService.ShowErrorAsync(L("InvalidDeviceIp"));
            }
        }
        partial void OnIsSamsungLoginActiveChanged(bool value)
        {
            CancelSamsungLoginCommand.NotifyCanExecuteChanged();
        }
        private void OnSamsungLoginStarted()
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsSamsungLoginActive = true;
            });
        }
        private void DisposeSamsungCts()
        {
            _samsungLoginCts?.Cancel();
            _samsungLoginCts?.Dispose();
            _samsungLoginCts = null;
        }
        public void Dispose()
        {
            DisposeSamsungCts();
            DisposeInitializationCts();
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
        }

        private void DisposeInitializationCts()
        {
            _initializationCts?.Cancel();
            _initializationCts?.Dispose();
            _initializationCts = null;
        }

    }
}
