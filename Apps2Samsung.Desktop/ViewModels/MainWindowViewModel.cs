using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Design;
using Apps2Samsung.Update;
using Avalonia.Media;
using Apps2Samsung.Styles;
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
using Apps2Samsung.Extensions;

namespace Apps2Samsung.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly ITizenInstallerService _tizenInstaller;

        // Handed to the toolbox so a system app can be launched over the developer channel, which is
        // the only one that reaches a platform app (#641).
        private readonly ISdbEngine _sdbEngine;
        private readonly IDialogService _dialogService;

        // SSL warnings (#656) raised before the main window opened; shown by InitializeAsync.
        private readonly List<string> _pendingSslWarnings = new();
        private bool _windowOpened;
        private readonly INetworkService _networkService;
        private readonly ILocalizationService _localizationService;
        private readonly IThemeService _themeService;
        private readonly IUpdaterService _updaterService;
        private readonly IUpdateDialogService _updateDialogService;
        private readonly FileHelper _fileHelper;
        private readonly PackageHelper _packageHelper;
        private readonly SettingsWindowViewModel _settingsViewModel;
        private readonly AddLatestRelease _addLatestRelease;
        private readonly ProviderManifestService _providerManifestService;
        private CancellationTokenSource? _samsungLoginCts;
        private CancellationTokenSource? _initializationCts;

        [ObservableProperty]
        private ObservableCollection<GitHubRelease> releases = new ObservableCollection<GitHubRelease>();

        // Every entry the providers yielded; Releases is the slice of it the category filter lets through.
        private List<GitHubRelease> _allReleases = new();
        private GitHubRelease? _customEntry;

        [ObservableProperty]
        private ObservableCollection<CategoryOption> categories = new ObservableCollection<CategoryOption>();

        [ObservableProperty]
        private CategoryOption? selectedCategory;

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

        /// <summary>The TV's install percentage (0–100), shown as the bar under the status line.</summary>
        [ObservableProperty]
        private int installProgress;

        /// <summary>
        /// The bar is only on screen while the TV is reporting: the steps before (download, signing,
        /// push) report no percentage, and the bar is hidden again once the install returns.
        /// </summary>
        [ObservableProperty]
        private bool isInstallProgressVisible;

        [ObservableProperty]
        private bool isSamsungLoginActive;

        [ObservableProperty]
        private bool darkMode;

        [ObservableProperty]
        private string vpnWarning = string.Empty;
        /// <summary>What the TV card shows: built in <see cref="UpdateTvCard"/> from the selected
        /// TV, the scan / install state and the status line, so the card and the status never
        /// disagree.</summary>
        [ObservableProperty]
        private TvCardState tvCard = new(string.Empty, string.Empty, TvLed.Off, string.Empty);

        // The sections (#755): Install, On the TV and Remote live in the main window behind a
        // sidebar; Settings keeps its window until its own redesign. The two TV-bound sections get a
        // view model for the selected TV when first shown, dropped when another TV is picked.
        public enum Section { Install, OnTheTv, Remote }
        [ObservableProperty]
        private Section selectedSection = Section.Install;
        public bool IsInstallSection => SelectedSection == Section.Install;
        public bool IsOnTheTvSection => SelectedSection == Section.OnTheTv;
        public bool IsRemoteSection => SelectedSection == Section.Remote;
        [ObservableProperty]
        private InstalledAppsViewModel? installedAppsVm;
        [ObservableProperty]
        private RemoteViewModel? remoteVm;
        public bool HasSelectedTv => SelectedDevice is { } d && !string.IsNullOrWhiteSpace(d.IpAddress) && d.IpAddress != L("lblOther");
        private string SelectedTvLabel => SelectedDevice is null ? string.Empty
            : string.IsNullOrWhiteSpace(SelectedDevice.DisplayText) ? SelectedDevice.IpAddress : SelectedDevice.DisplayText;
        public string LblInstall => L("lblInstall");
        public string LblOnTheTv => L("lblOnTheTv");
        public string LblRemoteTab => L("lblRemote");
        public string LblSettingsTab => L("lblSettingsTab");
        public string LblSelectTvFirst => L("lblSelectTvFirst");

        partial void OnSelectedSectionChanged(Section value)
        {
            OnPropertyChanged(nameof(IsInstallSection));
            OnPropertyChanged(nameof(IsOnTheTvSection));
            OnPropertyChanged(nameof(IsRemoteSection));
        }

        [RelayCommand]
        private async Task ShowSectionAsync(string? name)
        {
            switch (name)
            {
                case nameof(Section.OnTheTv): await ShowInstalledAppsAsync(); break;
                case nameof(Section.Remote): await ShowRemoteAsync(); break;
                default: SelectedSection = Section.Install; break;
            }
        }

        /// <summary>Another TV: the TV-bound sections start over the next time they are shown.</summary>
        private void DropTvSections()
        {
            InstalledAppsVm?.Dispose();
            InstalledAppsVm = null;
            var remote = RemoteVm;
            RemoteVm = null;
            if (remote is not null)
                _ = remote.DisposeAsync();
        }

        // The catalog (#755): a search field, category chips with counts, and the grouped list of
        // apps; tapping a row opens the app sheet (AppDetailWindow), which hands back the asset to
        // install. Releases / SelectedRelease / SelectedAsset stay the install pipeline's inputs;
        // the list is a view over them.
        private readonly HttpClient _httpClient;
        private ProviderManifest? _manifest;
        [ObservableProperty]
        private string searchText = string.Empty;
        public ObservableCollection<CatalogGroupView> CatalogGroupsView { get; } = new();
        // Categories the user expanded with "Show all".
        private readonly HashSet<string> _expandedCategories = new();
        // The apps the user starred (#755 favorites): a group of their own at the top of the list.
        private readonly HashSet<string> _favorites = new(
            (AppSettings.Default.FavoriteApps ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

        private void SetFavorite(string name, bool isFavorite)
        {
            if (isFavorite) _favorites.Add(name); else _favorites.Remove(name);
            AppSettings.Default.FavoriteApps = string.Join(";", _favorites);
            AppSettings.Default.Save();
            RebuildCatalogRows();
        }
        [ObservableProperty]
        private bool isCatalogEmpty;
        [ObservableProperty]
        private string catalogFoot = string.Empty;
        public string CustomFileSubline => string.IsNullOrEmpty(CustomWgtPath)
            ? L("lblInstallOwnFile")
            : string.Join(", ", CustomWgtPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileName));
        public string LblYourOwnPackage => L("lblYourOwnPackage");
        public string LblSearchApps => L("lblSearchApps");
        public string LblNoAppMatches => L("lblNoAppMatches");

        partial void OnSearchTextChanged(string value) => RebuildCatalogRows();

        /// <summary>A catalog group as the list shows it: title, count, its rows and the
        /// "Show all" / "Show less" action.</summary>
        public sealed record CatalogGroupView(CatalogGroup Group, IReadOnlyList<CatalogRow> Rows, string ToggleText)
        {
            public string Title => Group.Title;
            public string Count => Group.Count.ToString();
            public bool CanToggle => Group.CanToggle;
            public bool IsEmpty => Rows.Count == 0;
        }

        /// <summary>A list row: the release, its letter tile and the one-line subline.</summary>
        public sealed record CatalogRow(GitHubRelease Release, string Letter, IBrush TileBrush, string Subline)
        {
            public string Name => Release.Name;
        }

        /// <summary>"Show all" / "Show less" on a group.</summary>
        [RelayCommand]
        private void ToggleGroup(CatalogGroupView? group)
        {
            if (group is null)
                return;
            if (!_expandedCategories.Remove(group.Group.CategoryId))
                _expandedCategories.Add(group.Group.CategoryId);
            RebuildCatalogRows();
        }

        private CatalogRow RowFor(GitHubRelease release)
        {
            var category = AppCategories.DisplayName(release.Category, L);
            var subline = release.HasVariants
                ? $"{category} · {string.Format(L("lblVariantsCount"), release.Assets.Count)}"
                : category;
            return new CatalogRow(release, AppTiles.Letter(release.Name),
                DesignTheme.Brush(AppTiles.ColorToken(release.Category, release.Name)), subline);
        }

        // The catalog as short groups per category (Core's CatalogGroups), or one flat result
        // group while searching.
        private void RebuildCatalogRows()
        {
            CatalogGroupsView.Clear();
            var all = _allReleases.Where(r => r != CustomEntry).ToList();
            var groups = CatalogGroups.Build(all, SearchText, _expandedCategories, L, _favorites);
            var any = false;
            foreach (var group in groups)
            {
                var rows = group.Rows.Select(RowFor).ToList();
                any |= rows.Count > 0;
                CatalogGroupsView.Add(new CatalogGroupView(group, rows, L(group.IsExpanded ? "lblShowLess" : "lblShowAll")));
            }
            IsCatalogEmpty = !any && !IsLoading;
            CatalogFoot = string.Format(L("lblCatalogFoot"), all.Count);
        }

        /// <summary>The TV sheet from the "Change TV" chip: pick a found TV, enter an IP, or rescan.</summary>
        [RelayCommand]
        private async Task ChangeTvAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;
            var found = AvailableDevices.Where(d => !string.IsNullOrWhiteSpace(d.IpAddress) && d.IpAddress != L("lblOther")).ToList();
            var result = await new Views.TvPickerWindow(found, SelectedDevice, L).ShowDialog<Views.TvPickerResult?>(desktop.MainWindow);
            if (result is null)
                return;
            if (result.Rescan)
                await LoadDevicesAsync();
            else if (result.Manual)
                await PromptForManualIpAsync();
            else if (result.Device is not null)
                SelectedDevice = result.Device;
        }

        /// <summary>Opens the app sheet; installs the asset it hands back.</summary>
        [RelayCommand]
        private async Task OpenAppAsync(CatalogRow? row)
        {
            if (row is null || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return;
            var device = SelectedDevice;
            var hasTv = device is not null && !string.IsNullOrWhiteSpace(device.IpAddress) && device.IpAddress != L("lblOther");
            var tvName = !hasTv ? null : string.IsNullOrWhiteSpace(device!.DeviceName) ? device.IpAddress : device.DeviceName;
            var sheet = new AppDetailViewModel(
                row.Release,
                CatalogPreviews.For(_manifest, row.Release),
                row.Subline,
                tvName,
                IsSelectedDeviceReady,
                _httpClient,
                L,
                _favorites.Contains(row.Release.Name),
                SetFavorite);
            var asset = await new Views.AppDetailWindow(sheet).ShowDialog<Asset?>(desktop.MainWindow);
            if (asset is null)
                return;
            CustomWgtPath = string.Empty;
            SelectedRelease = row.Release;
            SelectedAsset = asset;
            if (DownloadAndInstallCommand.CanExecute(null))
                await DownloadAndInstallAsync();
        }

        /// <summary>The "Your own package" row: pick a .wgt / .tpk, then install it on the selected TV.</summary>
        [RelayCommand]
        private async Task InstallOwnFileAsync()
        {
            await BrowseWgtAsync();
            if (string.IsNullOrEmpty(CustomWgtPath))
                return;
            SelectedRelease = CustomEntry;
            if (DownloadAndInstallCommand.CanExecute(null))
                await DownloadAndInstallAsync();
            else
                SetStatus(IsSelectedDeviceReady ? "Ready" : "NoDevicesFound");
        }

        public bool HasVpnWarning => !string.IsNullOrEmpty(VpnWarning);

        partial void OnVpnWarningChanged(string value) => OnPropertyChanged(nameof(HasVpnWarning));

        private void RefreshVpnWarning()
        {
            var adapter = _networkService.GetActiveVpnAdapterName();
            VpnWarning = string.IsNullOrEmpty(adapter)
                ? string.Empty
                : string.Format(L("vpnDetectedWarning"), adapter);
        }

        private string _currentStatusKey = string.Empty;

        /// <summary>
        /// Install percentage sink for a run: shows the bar and puts the number in the status line.
        /// Synchronous on purpose (see <see cref="SyncProgress{T}"/>) so values arrive in the TV's order;
        /// the dispatcher post keeps that order on the UI thread.
        /// </summary>
        private IProgress<int> InstallProgressSink => new SyncProgress<int>(percent => Dispatcher.UIThread.Post(() =>
        {
            InstallProgress = percent;
            IsInstallProgressVisible = true;
            StatusBar = string.Format(L("InstallingPackageProgress"), percent);
        }));

        private void ResetInstallProgress()
        {
            IsInstallProgressVisible = false;
            InstallProgress = 0;
        }

        private string? _downloadedPackagePath;
        private string L(string key) => _localizationService.GetString(key);

        public bool EnableDevicesInput => !IsLoadingDevices;
        public string LblRelease => _localizationService.GetString("lblApp");
        public string LblCategory => _localizationService.GetString("lblCategory");
        // A folded community app offers variants (forks, per-Tizen builds), everything else versions.
        public string LblVersion => _localizationService.GetString(SelectedRelease?.HasVariants == true ? "lblVariant" : "lblVersion");
        public string LblSelectTv => _localizationService.GetString("lblSelectTv");
        public string DownloadAndInstall => _localizationService.GetString("DownloadAndInstall");
        public string lblCustomWgt => _localizationService.GetString("lblCustomWgt");
        public string SelectWGT => _localizationService.GetString("SelectWGT");
        public static string FooterText =>
            $"{AppSettings.Default.AppVersion} " +
            $"- Copyright (c) {DateTime.Now.Year} - MIT License - Patrick Stel";

        public MainWindowViewModel(
            ITizenInstallerService tizenInstaller,
            ISdbEngine sdbEngine,
            IDialogService dialogService,
            INetworkService networkService,
            ILocalizationService localizationService,
            IThemeService themeService,
            IUpdaterService updaterService,
            IUpdateDialogService updateDialogService,
            HttpClient httpClient,
            PackageHelper packageHelper,
            FileHelper fileHelper,
            SettingsWindowViewModel settingsViewModel,
            ResolvedCatalogService resolvedCatalog
        )
        {
            _tizenInstaller = tizenInstaller;
            _sdbEngine = sdbEngine;
            _dialogService = dialogService;
            _networkService = networkService;
            _packageHelper = packageHelper;
            _localizationService = localizationService;
            _themeService = themeService;
            _updaterService = updaterService;
            _updateDialogService = updateDialogService;
            _fileHelper = fileHelper;
            _settingsViewModel = settingsViewModel;
            _httpClient = httpClient;

            // Snapshot-first: the pre-resolved catalog serves the manifest's providers; the GitHub
            // API is only asked for what it lacks.
            _addLatestRelease = new AddLatestRelease(httpClient, snapshot: resolvedCatalog);
            _providerManifestService = new ProviderManifestService(httpClient);

            // A server certificate that failed validation (#656): tell the user, once per host, the
            // way the other warnings are shown. Fired from the HTTP stack, so hop to the UI thread;
            // and the dialog needs the main window, so anything raised before it opened waits in
            // _pendingSslWarnings until InitializeAsync (which the window's Opened event calls).
            GitHubAuthHandler.CertificateValidationIssue += (host, errors) =>
                Dispatcher.UIThread.Post(() =>
                {
                    var message = string.Format("statusSslWarning".Localized(), host, errors);
                    if (_windowOpened)
                        _ = _dialogService.ShowMessageAsync(L("lblSslWarningTitle"), message);
                    else
                        _pendingSslWarnings.Add(message);
                });

            _localizationService.LanguageChanged += OnLanguageChanged;
            _themeService.ThemeChanged += OnThemeChanged;

            // Initialize dark mode state from settings
            DarkMode = AppSettings.Default.DarkMode;
        }


        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshLocalizedProperties();
            RefreshVpnWarning();

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
            UpdateTvCard();
            OnPropertyChanged(nameof(LblInstall));
            OnPropertyChanged(nameof(LblOnTheTv));
            OnPropertyChanged(nameof(LblRemoteTab));
            OnPropertyChanged(nameof(LblSettingsTab));
            OnPropertyChanged(nameof(LblSelectTvFirst));
            OnPropertyChanged(nameof(CustomFileSubline));
            OnPropertyChanged(nameof(LblYourOwnPackage));
            OnPropertyChanged(nameof(LblSearchApps));
            OnPropertyChanged(nameof(LblNoAppMatches));
            RebuildCatalogRows();
            OnPropertyChanged(nameof(LblRelease));
            OnPropertyChanged(nameof(LblVersion));
            OnPropertyChanged(nameof(LblCategory));
            RebuildCategories();
            OnPropertyChanged(nameof(LblSelectTv));
            OnPropertyChanged(nameof(DownloadAndInstall));
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(StatusBar));
            OnPropertyChanged(nameof(lblCustomWgt));
            OnPropertyChanged(nameof(SelectWGT));
        }

        partial void OnSelectedCategoryChanged(CategoryOption? value) => ApplyCategoryFilter();

        partial void OnSelectedReleaseChanged(GitHubRelease? value)
        {
            OnPropertyChanged(nameof(LblVersion));
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
            OnPropertyChanged(nameof(CustomFileSubline));

            DownloadAndInstallCommand?.NotifyCanExecuteChanged();
            DownloadCommand?.NotifyCanExecuteChanged();
        }

        partial void OnSelectedDeviceChanged(NetworkDevice? value)
        {
            if (value?.IpAddress == L("lblOther"))
                _ = PromptForManualIpAsync();
            else if (value != null && !value.DebugPortOpen && !string.IsNullOrWhiteSpace(value.IpAddress))
                SetStatus(DeviceNotReadyStatusKey(value)); // detected TV, but not installable yet

            RefreshCanExecuteChanged();
            AppSettings.Default.TvIp = value?.IpAddress ?? string.Empty;
            AppSettings.Default.Save();
            UpdateTvCard();
            OnPropertyChanged(nameof(HasSelectedTv));
            DropTvSections();
            if (SelectedSection == Section.OnTheTv) _ = ShowInstalledAppsAsync();
            else if (SelectedSection == Section.Remote) _ = ShowRemoteAsync();
        }

        partial void OnStatusBarChanged(string value) => UpdateTvCard();
        partial void OnInstallProgressChanged(int value) => UpdateTvCard();
        partial void OnIsInstallProgressVisibleChanged(bool value) => UpdateTvCard();
        /// <summary>
        /// The TV card (Core's <see cref="TvCardStates"/>) for the selected TV, with this head's
        /// state on top: the LED pulses while anything runs, the status line is the card's message,
        /// and the TV's install percentage is its bar.
        /// </summary>
        private void UpdateTvCard()
        {
            var localIp = AppSettings.Default.LocalIp;
            var device = SelectedDevice;
            TvCardState state;
            if (IsLoadingDevices)
                state = TvCardStates.Scanning(L);
            else if (device is null || string.IsNullOrWhiteSpace(device.IpAddress) || device.IpAddress == L("lblOther"))
                state = TvCardStates.NoTv(L, localIp);
            else
                state = TvCardStates.ForDevice(device, localIp, L);

            var busy = IsLoadingDevices || IsLoading || IsInstallProgressVisible || IsSamsungLoginActive;
            TvCard = state with
            {
                Led = busy ? TvLed.Busy : state.Led,
                Message = string.IsNullOrWhiteSpace(StatusBar) ? state.Message : StatusBar,
                Progress = IsInstallProgressVisible ? InstallProgress : null,
            };
        }

        partial void OnIsLoadingChanged(bool value)
        {
            RefreshCanExecuteChanged();
            UpdateTvCard();
            IsCatalogEmpty = !CatalogGroupsView.Any(g => !g.IsEmpty) && !value;
        }

        partial void OnIsLoadingDevicesChanged(bool value)
        {
            OnPropertyChanged(nameof(EnableDevicesInput));
            UpdateTvCard();
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
            _windowOpened = true;
            foreach (var message in _pendingSslWarnings)
                await _dialogService.ShowMessageAsync(L("lblSslWarningTitle"), message);
            _pendingSslWarnings.Clear();

            // Create a new CTS for initialization that can be cancelled when update dialog shows
            _initializationCts?.Cancel();
            _initializationCts?.Dispose();
            _initializationCts = new CancellationTokenSource();
            var token = _initializationCts.Token;

            try
            {
                // Check for updates first (non-blocking, runs in background)
                _ = CheckForUpdatesAsync();

                // Evaluate the VPN/tunnel warning up front, independent of the (slow) network
                // scan, so the banner appears immediately on startup.
                RefreshVpnWarning();

                // A leftover Tizen SDK "sdb" server can hold the TV's single debug connection, which
                // makes our own connect fail; clear those before scanning.
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
                // Download update: the status line carries the bytes ("12.3 MB of 87.3 MB"), the
                // card's bar the percentage.
                IsInstallProgressVisible = true;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        InstallProgress = p.Percent;
                        StatusBar = $"{L("UpdateDownloading")} {p.Text}";
                    });
                });

                StatusBar = L("UpdateDownloading");
                var downloadedPath = await _updaterService.DownloadUpdateAsync(updateResult.DownloadUrl, progress);

                // Apply: no dialog to confirm in between, the status line says what happens.
                StatusBar = L("UpdateInstalling");
                InstallProgress = 100;
                var success = await _updaterService.ApplyUpdateAsync(downloadedPath);

                if (success)
                {
                    // The update script waits for this process to end before it swaps the files
                    // and restarts the app. Shutdown closes the windows; the hard exit after it
                    // makes sure a lingering background thread (the in-process sdb server, a
                    // socket) can't keep the process, and so the script, waiting forever.
                    if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                        desktop.Shutdown();
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1500);
                        Environment.Exit(0);
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Automatic update failed: {ex}");
                ResetInstallProgress();
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
                // Note: Releases always contains the "Custom WGT / TPK" entry, so we
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
            ResetInstallProgress();

            // Auto-request Partner signing when the selected package's manifest declares
            // cert_level: partner (silent bump; the global toggle still forces Partner too).
            AppSettings.Default.RequiresPartnerSigning = SelectedRelease?.RequiresPartner ?? false;

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
                            onSamsungLoginStarted: OnSamsungLoginStarted,
                            installProgress: InstallProgressSink);
                    }
                    finally
                    {
                        ResetInstallProgress();
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
                                onSamsungLoginStarted: OnSamsungLoginStarted,
                                installProgress: InstallProgressSink);
                        }
                        finally
                        {
                            ResetInstallProgress();
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
                await _dialogService.ShowErrorAsync(string.Format("statusOpenFailed".Localized(), L("catalogTitle"), ex));
            }
        }

        [RelayCommand]
        private async Task ShowInstalledAppsAsync()
        {
            // The "On the TV" section. Its view model is built for the selected TV the first time
            // the section is shown and dropped when another TV is picked (OnSelectedDeviceChanged).
            SelectedSection = Section.OnTheTv;
            if (InstalledAppsVm is not null || !HasSelectedTv)
                return;
            try
            {
                var vm = new InstalledAppsViewModel(_tizenInstaller, _dialogService, SelectedDevice!.IpAddress, SelectedTvLabel);
                vm.OnRequestClose += () => SelectedSection = Section.Install;
                InstalledAppsVm = vm;
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(string.Format("statusOpenFailed".Localized(), L("lblInstalledApps"), ex));
            }
        }
        [RelayCommand]
        private async Task ShowDeviceInfoAsync()
        {
            try
            {
                if (SelectedDevice is null ||
                    string.IsNullOrWhiteSpace(SelectedDevice.IpAddress) ||
                    SelectedDevice.IpAddress == L("lblOther"))
                {
                    await _dialogService.ShowMessageAsync(L("lblTvInformation"), L("lblSelectTvFirst"));
                    return;
                }

                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return;

                var label = string.IsNullOrWhiteSpace(SelectedDevice.DisplayText)
                    ? SelectedDevice.IpAddress
                    : SelectedDevice.DisplayText;
                // This computer's IP: the interface chosen in Settings, else whatever the OS routes out of.
                var localIp = AppSettings.Default.LocalIp;
                if (string.IsNullOrWhiteSpace(localIp))
                {
                    try { localIp = _networkService.GetLocalIPAddress(); }
                    catch { localIp = null; }
                }
                var vm = new DeviceInfoViewModel(_tizenInstaller, SelectedDevice.IpAddress, label, SelectedDevice.DebugPortOpen, localIp);
                var window = new Views.DeviceInfoWindow(vm);
                await window.ShowDialog(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(string.Format("statusOpenFailed".Localized(), L("lblTvInformation"), ex));
            }
        }

        /// <summary>
        /// Opens the remote for the selected TV. Unlike the installer views this needs no Developer
        /// Mode or debug port — only a TV on the network — so it is offered for any selected TV.
        /// </summary>
        [RelayCommand]
        private async Task ShowRemoteAsync()
        {
            // The "Remote" section; same lifetime as the installed-apps one. The remote needs only
            // a TV on the network, so a not-ready TV is fine here.
            SelectedSection = Section.Remote;
            if (RemoteVm is not null || !HasSelectedTv)
                return;
            try
            {
                // The scan knows the TV's MAC while it is awake; keep it so the remote can wake the
                // set later, when it answers nothing at all.
                if (!string.IsNullOrWhiteSpace(SelectedDevice!.MacAddress))
                    Helpers.Core.RemoteStore.SetMac(SelectedDevice.IpAddress, SelectedDevice.MacAddress!);
                var vm = new RemoteViewModel(SelectedDevice.IpAddress, SelectedTvLabel);
                vm.OnRequestClose += () => SelectedSection = Section.Install;
                RemoteVm = vm;
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(string.Format("statusOpenFailed".Localized(), L("lblRemote"), ex));
            }
        }
        /// <summary>
        /// Opens the TV toolbox (#635): launching an app by id and sending a documented service-menu
        /// combination. Like the remote it rides the remote channel, so it needs no Developer Mode and
        /// is offered for any TV on the network — including one the installer itself couldn't use.
        /// </summary>
        [RelayCommand]
        private async Task ShowTvToolboxAsync()
        {
            try
            {
                if (SelectedDevice is null ||
                    string.IsNullOrWhiteSpace(SelectedDevice.IpAddress) ||
                    SelectedDevice.IpAddress == L("lblOther"))
                {
                    await _dialogService.ShowMessageAsync(L("lblToolbox"), L("lblRemoteSelectTvFirst"));
                    return;
                }

                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return;

                var label = string.IsNullOrWhiteSpace(SelectedDevice.DisplayText)
                    ? SelectedDevice.IpAddress
                    : SelectedDevice.DisplayText;

                // The scan knows the TV's MAC while it is awake; keep it so a sleeping set can still
                // be woken later (same store the remote uses).
                if (!string.IsNullOrWhiteSpace(SelectedDevice.MacAddress))
                    Helpers.Core.RemoteStore.SetMac(SelectedDevice.IpAddress, SelectedDevice.MacAddress!);

                // The toolbox's debug agent is a .wgt like any other: the normal pipeline (certificate,
                // resign, push) puts it on the TV when the set doesn't have it yet (#34).
                var tvIp = SelectedDevice.IpAddress;
                Func<string, Action<string>, Task<bool>> installWgt = async (wgtPath, report) =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var result = await _tizenInstaller.InstallPackageAsync(wgtPath, tvIp, timeout.Token, message => report(message));
                    return result.Success;
                };

                var vm = new TvToolboxViewModel(tvIp, label, _sdbEngine, installWgt);
                var window = new Views.TvToolboxWindow(vm);
                await window.ShowDialog(desktop.MainWindow);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(string.Format("statusOpenFailed".Localized(), L("lblToolbox"), ex));
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

        // A real TV that is actually installable: has an IP, isn't the "Other" placeholder,
        // and its Tizen debug port (26101) is reachable. TVs detected only via the REST API
        // (Developer Mode not fully active) are not installable.
        private bool IsSelectedDeviceReady =>
            SelectedDevice != null &&
            !string.IsNullOrWhiteSpace(SelectedDevice.IpAddress) &&
            SelectedDevice.IpAddress != L("lblOther") &&
            SelectedDevice.DebugPortOpen;

        private bool CanDownload()
        {
            if (!string.IsNullOrEmpty(AppSettings.Default.CustomWgtPath))
            {
                var files = AppSettings.Default.CustomWgtPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
                return files.All(File.Exists) &&
                       !IsLoading &&
                       IsSelectedDeviceReady;
            }

            return !IsLoading &&
                   SelectedRelease != null &&
                   SelectedAsset != null &&
                   IsSelectedDeviceReady;
        }


        private bool CanInstall()
        {
            // If custom wgt path is set, allow install without _downloadedPackagePath
            if (!string.IsNullOrEmpty(AppSettings.Default.CustomWgtPath))
            {
                var files = AppSettings.Default.CustomWgtPath.Split(';');
                return files.All(File.Exists) &&
                       IsSelectedDeviceReady;
            }

            // Otherwise fallback to _downloadedPackagePath logic
            return !string.IsNullOrEmpty(_downloadedPackagePath) &&
                   File.Exists(_downloadedPackagePath) &&
                   IsSelectedDeviceReady;
        }

        // Actionable status-bar message for a TV that was detected but isn't installable yet
        // (debug port closed). The classification is shared with the mobile head (Core).
        private static string DeviceNotReadyStatusKey(NetworkDevice device) =>
            TizenDeviceReadiness.MessageKey(
                TizenDeviceReadiness.WhyNotReady(device, AppSettings.Default.LocalIp));


        // True once the release list holds something other than the always-present
        // "Custom WGT / TPK" entry — i.e. providers actually loaded.
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
                _manifest = manifest;
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

                    var fetched = await _addLatestRelease.GetProviderReleasesAsync(provider, take);
                    var release = fetched.Releases;
                    if (release.Count == 0)
                        continue;

                    // Apps declaring cert_level: partner auto-request Partner signing at install.
                    bool requiresPartner = string.Equals(provider.CertLevel, "partner", StringComparison.OrdinalIgnoreCase);

                    if (provider.ExpandAssets)
                    {
                        // The community bundle: one entry per file, or one per app when the
                        // release's catalog.json groups files (forks, per-Tizen builds) into
                        // variants. Without a catalog this is the flat list it always was.
                        var catalog = fetched.Catalog
                            ?? await _addLatestRelease.GetCommunityCatalogAsync(release[0].CatalogUrl);
                        list.AddRange(CommunityAppList.Expand(release, catalog, requiresPartner));
                    }
                    else
                    {
                        var category = AppCategories.Normalize(provider.Category);
                        foreach (var r in release)
                        {
                            r.RequiresPartner = requiresPartner;
                            r.Category = category;
                        }
                        list.AddRange(release);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Show every app alphabetically (A-Z, 0-9) regardless of manifest/fetch order.
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                _allReleases = list;
                RebuildCategories();
                ApplyCategoryFilter();
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
                // Always add the custom WGT / TPK option, regardless of GitHub failures
                if (!Releases.Contains(CustomEntry))
                    Releases.Add(CustomEntry);
                IsLoading = false;
            }
        }

        // The synthetic "Custom WGT / TPK" row: one instance, so the selection survives a re-filter.
        private GitHubRelease CustomEntry => _customEntry ??= new GitHubRelease
        {
            Name = Constants.AppIdentifiers.CustomWgtFile,
            TagName = string.Empty,
            PublishedAt = string.Empty,
            Url = string.Empty,
            Assets = new List<Asset>()
        };

        // The category dropdown: "All categories" plus every category that has at least one app,
        // each with its count. Keeps the current choice when that category still exists.
        private void RebuildCategories()
        {
            var keep = SelectedCategory?.Id ?? AppCategories.All;
            Categories.Clear();
            foreach (var (id, count) in CommunityAppList.Counts(_allReleases))
                Categories.Add(new CategoryOption(id, AppCategories.DisplayName(id, L), count));
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == keep) ?? Categories.FirstOrDefault();
        }

        // Releases = the apps in the chosen category, plus the custom-file row that is never filtered
        // away. The selected app stays selected while its category is in view.
        private void ApplyCategoryFilter()
        {
            var keep = SelectedRelease;
            var id = SelectedCategory?.Id ?? AppCategories.All;

            Releases.Clear();
            foreach (var r in CommunityAppList.Filter(_allReleases, id))
                Releases.Add(r);
            Releases.Add(CustomEntry);

            SelectedRelease = keep != null && Releases.Contains(keep) ? keep : null;
            RebuildCatalogRows();
        }

        private async Task LoadDevicesAsync(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            IsLoadingDevices = true;
            AvailableDevices.Clear();
            RefreshVpnWarning();

            try
            {
                string? selectedIp = SelectedDevice?.IpAddress;

                // Scan + Developer-Mode enrichment, shared with the mobile head (Core).
                var devices = await TizenDeveloperInfo.ScanAsync(_networkService, cancellationToken, virtualScan);
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
                    // "Ready" only if something is actually installable; otherwise guide the user
                    // on the first detected-but-not-ready TV (debug port closed).
                    var firstNotReady = AvailableDevices.FirstOrDefault(d => !d.DebugPortOpen);
                    if (AvailableDevices.Any(d => d.DebugPortOpen) || firstNotReady == null)
                        SetStatus("Ready");
                    else
                        SetStatus(DeviceNotReadyStatusKey(firstNotReady));
                }

                if (AvailableDevices.Any())
                {
                    if (SelectedDevice == null)
                        SelectedDevice = AvailableDevices.FirstOrDefault(d => d.DebugPortOpen) ?? AvailableDevices[0];
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
                await _dialogService.ShowErrorAsync(string.Format("statusLoadDevicesFailed".Localized(), ex));
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

            var samsungDevice = await TizenDeveloperInfo.ReadAsync(device);

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
            UpdateTvCard();
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
