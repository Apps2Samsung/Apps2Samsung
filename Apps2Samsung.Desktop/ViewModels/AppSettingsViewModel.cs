using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// App-wide settings that apply regardless of which app is being installed
    /// (language, signing certificate, local network interface, GitHub token,
    /// install options, dark mode, diagnostics). App-specific settings live in
    /// their own <see cref="IAppSettingsProvider"/> sections.
    /// </summary>
    public partial class AppSettingsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;
        private readonly IDialogService _dialogService;
        private readonly CertificateHelper _certificateHelper;
        private readonly INetworkService _networkService;
        private readonly IThemeService _themeService;

        [ObservableProperty]
        private LanguageOption? selectedLanguage;

        [ObservableProperty]
        private ExistingCertificates? selectedCertificateObject;

        [ObservableProperty]
        private string selectedCertificate = string.Empty;

        [ObservableProperty]
        private string localIP = string.Empty;

        [ObservableProperty]
        private bool tryOverwrite;

        [ObservableProperty]
        private bool deletePreviousInstall;

        [ObservableProperty]
        private bool forceSamsungLogin;

        // Automatic / Public / Partner, the same choice as the phone (Core CertificatePreference).
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CertificateAutomatic), nameof(CertificatePublic), nameof(CertificatePartner))]
        private string certificatePreference = Apps2Samsung.Certificate.CertificatePreference.Automatic;

        // One per segment of the switch (RadioButtons bind a bool each).
        public bool CertificateAutomatic
        {
            get => CertificatePreference == Apps2Samsung.Certificate.CertificatePreference.Automatic;
            set { if (value) CertificatePreference = Apps2Samsung.Certificate.CertificatePreference.Automatic; }
        }
        public bool CertificatePublic
        {
            get => CertificatePreference == Apps2Samsung.Certificate.CertificatePreference.Public;
            set { if (value) CertificatePreference = Apps2Samsung.Certificate.CertificatePreference.Public; }
        }
        public bool CertificatePartner
        {
            get => CertificatePreference == Apps2Samsung.Certificate.CertificatePreference.Partner;
            set { if (value) CertificatePreference = Apps2Samsung.Certificate.CertificatePreference.Partner; }
        }

        // Public and Partner are Samsung's names for the two levels, not translated (as on the phone).
        public string LblCertificatePublic => Apps2Samsung.Certificate.CertificatePreference.Public;
        public string LblCertificatePartner => Apps2Samsung.Certificate.CertificatePreference.Partner;

        [ObservableProperty]
        private bool includeBetaUpdates;

        [ObservableProperty]
        private bool rtlReading;

        [ObservableProperty]
        private bool openAfterInstall;

        [ObservableProperty]
        private bool keepWGTFile;

        // The catalog ⋯ > Show adult (18+) apps (#850). Turning it on first asks "I am 18 or
        // older"; the stored value only changes once that is confirmed, and the switch springs back
        // when it is not. Fires ShowAdultAppsChanged for the main window to refilter the catalog.
        [ObservableProperty]
        private bool showAdultApps;
        private bool _revertingShowAdultApps;
        public event Action? ShowAdultAppsChanged;

        // The catalog ⋯ > Show root apps: on by default, no confirmation (the install asks).
        // Fires ShowRootAppsChanged for the main window to refilter the catalog.
        [ObservableProperty]
        private bool showRootApps;
        public event Action? ShowRootAppsChanged;

        [ObservableProperty]
        private bool darkMode;

        [ObservableProperty]
        private string gitHubToken = string.Empty;

        [ObservableProperty]
        private bool showGitHubToken = false;

        [ObservableProperty]
        private string manualDuids = string.Empty;

        [ObservableProperty]
        private NetworkInterfaceOption? selectedNetworkInterface;

        public ObservableCollection<LanguageOption> AvailableLanguages { get; }
        // "Own certificate": "None" first, then the certificates the user added to the certificate
        // folder. The app's generated ones are what the Automatic/Public/Partner switch picks, so
        // they're not in here. The row only shows when there is something to pick.
        public ObservableCollection<ExistingCertificates> AvailableCertificates { get; } = new();

        [ObservableProperty]
        private bool hasOwnCertificates;
        public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; } = new();

        public char GitHubTokenPasswordChar => ShowGitHubToken ? '\0' : '*';

        // Localized labels
        public string LblTabMainSettings => _localizationService.GetString("lblTabMainSettings");
        public string LblMainSettings => _localizationService.GetString("lblMainSettings");
        public string LblLanguage => _localizationService.GetString("lblLanguage");
        public string LblCertificate => _localizationService.GetString("lblCertifcate");
        public string LblLocalIP => _localizationService.GetString("lblLocalIP");
        public string LblTryOverwrite => _localizationService.GetString("lblTryOverwrite");
        public string LblLaunchOnInstall => _localizationService.GetString("lblLaunchOnInstall");
        public string LblRememberIp => _localizationService.GetString("lblRememberIp");
        public string LblDeletePrevious => _localizationService.GetString("lblDeletePrevious");
        public string LblForceLogin => _localizationService.GetString("lblForceSamsungLogin");
        public string LblIncludeBetaUpdates => _localizationService.GetString("lblIncludeBetaUpdates");
        public string LblShowAdultApps => _localizationService.GetString("lblShowAdultApps");
        public string LblShowRootApps => _localizationService.GetString("lblShowRootApps");
        public string LblRTL => _localizationService.GetString("lblRTL");
        public string LblKeepWGTFile => _localizationService.GetString("lblKeepWGTFile");
        public string LblSettingsHeader => _localizationService.GetString("lblSettings");
        public string LblGitHubToken => _localizationService.GetString("lblGitHubToken");
        public string LblGitHubTokenHint => _localizationService.GetString("lblGitHubTokenHint");
        public string LblManualDuids => _localizationService.GetString("lblManualDuids");
        public string LblManualDuidsHint => _localizationService.GetString("lblManualDuidsHint");
        public string LblOpenLogsFolder => _localizationService.GetString("lblOpenLogsFolder");

        public AppSettingsViewModel(
            ILocalizationService localizationService,
            CertificateHelper certificateHelper,
            INetworkService networkService,
            IThemeService themeService,
            IDialogService dialogService)
        {
            _localizationService = localizationService;
            _dialogService = dialogService;
            _certificateHelper = certificateHelper;
            _networkService = networkService;
            _themeService = themeService;

            _localizationService.LanguageChanged += OnLanguageChanged;
            _themeService.ThemeChanged += OnThemeChanged;
            AppSettings.PartnerSigningAutoEnabled += OnPartnerSigningAutoEnabled;

            AvailableLanguages = new ObservableCollection<LanguageOption>(
                _localizationService.AvailableLanguages
                    .Select(code => new LanguageOption
                    {
                        Code = code,
                        Name = _localizationService.GetDisplayName(code)
                    })
                    .OrderBy(lang => lang.Name)
            );

            InitializeMainSettings();
            _ = LoadNetworkInterfacesAsync();
            _ = InitializeCertificatesAsync();
        }

        // The installer switched the certificate to Partner for a package that requires it — mirror it
        // onto the switch so this (singleton) view model doesn't keep showing the pre-install value.
        private void OnPartnerSigningAutoEnabled() =>
            Dispatcher.UIThread.Post(() => CertificatePreference = AppSettings.Default.CertificatePreference);

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshLocalizedProperties();
        }

        private void OnThemeChanged(object? sender, bool isDarkMode)
        {
            DarkMode = isDarkMode;
        }

        private void RefreshLocalizedProperties()
        {
            OnPropertyChanged(nameof(LblTabMainSettings));
            OnPropertyChanged(nameof(LblMainSettings));
            OnPropertyChanged(nameof(LblLanguage));
            OnPropertyChanged(nameof(LblCertificate));
            OnPropertyChanged(nameof(LblLocalIP));
            OnPropertyChanged(nameof(LblTryOverwrite));
            OnPropertyChanged(nameof(LblLaunchOnInstall));
            OnPropertyChanged(nameof(LblRememberIp));
            OnPropertyChanged(nameof(LblDeletePrevious));
            OnPropertyChanged(nameof(LblForceLogin));
            OnPropertyChanged(nameof(LblIncludeBetaUpdates));
            OnPropertyChanged(nameof(LblShowAdultApps));
            OnPropertyChanged(nameof(LblShowRootApps));
            OnPropertyChanged(nameof(LblRTL));
            OnPropertyChanged(nameof(LblKeepWGTFile));
            OnPropertyChanged(nameof(LblSettingsHeader));
            OnPropertyChanged(nameof(LblGitHubToken));
            OnPropertyChanged(nameof(LblGitHubTokenHint));
            OnPropertyChanged(nameof(LblManualDuids));
            OnPropertyChanged(nameof(LblManualDuidsHint));
            OnPropertyChanged(nameof(LblOpenLogsFolder));
        }

        private void InitializeMainSettings()
        {
            // Use current language from LocalizationService or fallback to saved setting
            var currentLangCode = _localizationService.CurrentLanguage ?? AppSettings.Default.Language ?? "en";

            SelectedLanguage = AvailableLanguages
                .FirstOrDefault(lang => string.Equals(lang.Code, currentLangCode, StringComparison.OrdinalIgnoreCase))
                ?? AvailableLanguages.FirstOrDefault();

            DeletePreviousInstall = AppSettings.Default.DeletePreviousInstall;
            ForceSamsungLogin = AppSettings.Default.ForceSamsungLogin;
            CertificatePreference = AppSettings.Default.CertificatePreference;
            IncludeBetaUpdates = AppSettings.Default.IncludeBetaUpdates;
            RtlReading = AppSettings.Default.RTLReading;
            LocalIP = AppSettings.Default.LocalIp ?? string.Empty;
            TryOverwrite = AppSettings.Default.TryOverwrite;
            OpenAfterInstall = AppSettings.Default.OpenAfterInstall;
            KeepWGTFile = AppSettings.Default.KeepWGTFile;
            ShowAdultApps = AppSettings.Default.ShowAdultApps;
            ShowRootApps = AppSettings.Default.ShowRootApps;
            DarkMode = AppSettings.Default.DarkMode;
            GitHubToken = AppSettings.Default.GitHubToken ?? string.Empty;
            ManualDuids = AppSettings.Default.ManualDuids ?? string.Empty;
        }

        // True while LoadNetworkInterfacesAsync restores the selection: a programmatic pick must
        // not overwrite the card the user chose (see OnSelectedNetworkInterfaceChanged).
        private bool _restoringNetworkInterfaces;

        /// <summary>
        /// Re-reads the network cards and this machine's local IP from them. Called before every
        /// TV scan and when the OS reports an address change: after a sleep the Wi-Fi interface is
        /// torn down and re-created, so the IP picked at startup can describe an interface that no
        /// longer exists, and the TV then looks not ready until a relaunch re-reads it (#922).
        /// </summary>
        public System.Threading.Tasks.Task RefreshNetworkInterfacesAsync() => LoadNetworkInterfacesAsync();

        private async System.Threading.Tasks.Task LoadNetworkInterfacesAsync()
        {
            try
            {
                var interfaces = await _networkService.GetNetworkInterfaceOptionsAsync();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    NetworkInterfaces.Clear();

                    foreach (var ni in interfaces)
                        NetworkInterfaces.Add(ni);

                    // Restore previous selection: match by name first (stable across DHCP changes),
                    // fall back to IP match, then default to first interface
                    var savedName = AppSettings.Default.SavedNetworkInterfaceName;
                    var savedIp = AppSettings.Default.LocalIp;
                    var restored =
                        (!string.IsNullOrEmpty(savedName)
                            ? NetworkInterfaces.FirstOrDefault(i => i.Name == savedName)
                            : null)
                        ?? (!string.IsNullOrEmpty(savedIp)
                            ? NetworkInterfaces.FirstOrDefault(i => i.IpAddress == savedIp)
                            : null)
                        ?? NetworkInterfaces.FirstOrDefault();

                    _restoringNetworkInterfaces = true;
                    try
                    {
                        SelectedNetworkInterface = restored;
                        // Same card, new address (DHCP, or the card came back after a wake): the
                        // setter above does not fire for an equal selection, so push the IP through.
                        if (restored is not null && AppSettings.Default.LocalIp != restored.IpAddress)
                        {
                            LocalIP = restored.IpAddress;
                            AppSettings.Default.LocalIp = restored.IpAddress;
                        }
                        // No card at all (offline): forget the stale address rather than keep
                        // comparing the TV against an interface that is gone.
                        if (restored is null)
                        {
                            LocalIP = string.Empty;
                            AppSettings.Default.LocalIp = string.Empty;
                        }
                    }
                    finally
                    {
                        _restoringNetworkInterfaces = false;
                    }
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to load network interfaces: {ex}");
            }
        }

        private async System.Threading.Tasks.Task InitializeCertificatesAsync()
        {
            var certificates = _certificateHelper.GetAvailableCertificates(
                AppSettings.CertificatePath, AppSettings.BundledCertificatePath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var savedCertName = AppSettings.Default.Certificate;
                foreach (var cert in certificates)
                {
                    // "Jelly2Sams (default)" is the "None" entry; the other generated "Jelly2Sams - …"
                    // profiles belong to the switch. The shipped "Jellyfin" certificate (older TVs
                    // get it by themselves) only stays listed while it is the saved pick.
                    if (cert.Name == Constants.AppIdentifiers.Jelly2SamsDefault)
                        cert.DisplayName = _localizationService.GetString("lblOwnCertificateNone");
                    else if (cert.Name.StartsWith(Constants.AppIdentifiers.Jelly2Sams, StringComparison.OrdinalIgnoreCase) ||
                             (cert.Name == Constants.AppIdentifiers.JellyfinAppName && cert.Name != savedCertName))
                        continue;
                    AvailableCertificates.Add(cert);
                }
                HasOwnCertificates = AvailableCertificates.Count > 1;

                var selectedCert = AvailableCertificates.FirstOrDefault(c => c.Name == savedCertName)
                    ?? AvailableCertificates.FirstOrDefault(c => c.Name == Constants.AppIdentifiers.Jelly2SamsDefault);

                if (selectedCert != null)
                    SelectedCertificate = selectedCert.Name;

                AppSettings.Default.ChosenCertificates = selectedCert;
            });
        }


        [ObservableProperty]
        private string backupStatus = string.Empty;

        // Export settings.json + the generated signing certificates to a single .zip the user can copy
        // to another computer or phone (#510). Uses the shared Core BackupService so the archive format
        // matches the mobile head.
        [RelayCommand]
        private async Task ExportBackup()
        {
            try
            {
                var storage = GetActiveStorageProvider();
                if (storage is null)
                    return;

                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export backup",
                    SuggestedFileName = "apps2samsung-backup.zip",
                    DefaultExtension = "zip",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Apps2Samsung backup") { Patterns = new[] { "*.zip" } }
                    }
                });
                if (file is null)
                    return;

                var settingsJson = JsonSerializer.Serialize(AppSettings.Default,
                    new JsonSerializerOptions { WriteIndented = true });

                await using (var stream = await file.OpenWriteAsync())
                    Apps2Samsung.Backup.BackupService.Export(stream, settingsJson, AppSettings.CertificatePath);

                BackupStatus = "statusBackupExported".Localized();
            }
            catch (Exception ex)
            {
                BackupStatus = string.Format("statusExportFailed".Localized(), ex.Message);
                Trace.WriteLine($"[Backup] export failed: {ex}");
            }
        }

        // Import a backup .zip: restore the certificates and merge the settings. Settings take effect on
        // next launch.
        [RelayCommand]
        private async Task ImportBackup()
        {
            try
            {
                var storage = GetActiveStorageProvider();
                if (storage is null)
                    return;

                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import backup",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Apps2Samsung backup") { Patterns = new[] { "*.zip" } }
                    }
                });
                var file = files.FirstOrDefault();
                if (file is null)
                    return;

                Apps2Samsung.Backup.BackupImportResult result;
                await using (var stream = await file.OpenReadAsync())
                    result = Apps2Samsung.Backup.BackupService.Import(stream, AppSettings.CertificatePath);

                if (!string.IsNullOrEmpty(result.SettingsJson))
                    MergeImportedSettings(result.SettingsJson!);

                BackupStatus = string.Format("statusImportedDesktop".Localized(), result.CertificateFilesRestored);
            }
            catch (Exception ex)
            {
                BackupStatus = string.Format("statusImportFailed".Localized(), ex.Message);
                Trace.WriteLine($"[Backup] import failed: {ex}");
            }
        }

        // Merge the imported settings.json into the current settings.json: imported keys win, and keys the
        // backup doesn't contain (e.g. desktop-only fields absent from a mobile export) keep their current
        // value, so importing never silently resets unrelated settings. Applied on next Load().
        private static void MergeImportedSettings(string importedJson)
        {
            var current = JsonNode.Parse(JsonSerializer.Serialize(AppSettings.Default))?.AsObject() ?? new JsonObject();
            var imported = JsonNode.Parse(importedJson)?.AsObject();
            if (imported is not null)
            {
                foreach (var kv in imported)
                    current[kv.Key] = kv.Value?.DeepClone();
            }

            var dir = Path.GetDirectoryName(AppSettings.FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(AppSettings.FilePath,
                current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static IStorageProvider? GetActiveStorageProvider()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return (desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow)?.StorageProvider;

            return null;
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            try
            {
                var logFolder = Apps2Samsung.Diagnostics.FileLog.DefaultLogDirectory;
                Directory.CreateDirectory(logFolder);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{logFolder}\"",
                        UseShellExecute = true
                    });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = $"\"{logFolder}\"",
                        UseShellExecute = false
                    });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{logFolder}\"",
                        UseShellExecute = false
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logFolder,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open Logs folder: {ex}");
            }
        }

        partial void OnSelectedNetworkInterfaceChanged(NetworkInterfaceOption? value)
        {
            if (value == null)
                return;

            LocalIP = value.IpAddress;
            AppSettings.Default.LocalIp = value.IpAddress;
            // Remember the card only when the user picked it (or nothing was remembered yet). A
            // restore that fell through to "first card" because the chosen one is momentarily down
            // must not replace the choice: when the Wi-Fi comes back it has to win again.
            if (!_restoringNetworkInterfaces || string.IsNullOrEmpty(AppSettings.Default.SavedNetworkInterfaceName))
                AppSettings.Default.SavedNetworkInterfaceName = value.Name;
            AppSettings.Default.Save();
        }

        partial void OnSelectedLanguageChanged(LanguageOption? value)
        {
            if (value is null)
                return;

            AppSettings.Default.Language = value.Code;
            AppSettings.Default.Save();

            // Update the global LocalizationService
            _localizationService.SetLanguage(value.Code);
        }

        partial void OnSelectedCertificateObjectChanged(ExistingCertificates? value)
        {
            if (value != null)
            {
                SelectedCertificate = value.Name;
                AppSettings.Default.Certificate = value.Name;
                AppSettings.Default.Save();
            }
        }

        partial void OnSelectedCertificateChanged(string value)
        {
            AppSettings.Default.Certificate = value;
            AppSettings.Default.Save();

            SelectedCertificateObject = AvailableCertificates.FirstOrDefault(c => c.Name == value);
            AppSettings.Default.ChosenCertificates = SelectedCertificateObject;
        }

        partial void OnLocalIPChanged(string value)
        {
            AppSettings.Default.LocalIp = value;
            AppSettings.Default.Save();
        }

        partial void OnTryOverwriteChanged(bool value)
        {
            AppSettings.Default.TryOverwrite = value;
            AppSettings.Default.Save();
        }

        partial void OnForceSamsungLoginChanged(bool value)
        {
            AppSettings.Default.ForceSamsungLogin = value;
            AppSettings.Default.Save();
        }

        partial void OnCertificatePreferenceChanged(string value)
        {
            AppSettings.Default.CertificatePreference = Apps2Samsung.Certificate.CertificatePreference.Normalize(value);
            AppSettings.Default.Save();
        }

        partial void OnIncludeBetaUpdatesChanged(bool value)
        {
            AppSettings.Default.IncludeBetaUpdates = value;
            AppSettings.Default.Save();
        }

        partial void OnShowAdultAppsChanged(bool value)
        {
            if (_revertingShowAdultApps || value == AppSettings.Default.ShowAdultApps)
                return;
            if (value)
            {
                _ = ConfirmShowAdultAppsAsync();
                return;
            }
            AppSettings.Default.ShowAdultApps = false;
            AppSettings.Default.Save();
            ShowAdultAppsChanged?.Invoke();
        }

        partial void OnShowRootAppsChanged(bool value)
        {
            if (value == AppSettings.Default.ShowRootApps)
                return;
            AppSettings.Default.ShowRootApps = value;
            AppSettings.Default.Save();
            ShowRootAppsChanged?.Invoke();
        }

        // The one-time "I am 18 or older" before adult entries get listed (#850).
        private async Task ConfirmShowAdultAppsAsync()
        {
            var confirmed = await _dialogService.ShowConfirmationAsync(
                _localizationService.GetString("lblAdultConfirmTitle"),
                _localizationService.GetString("lblAdultConfirmBody"),
                _localizationService.GetString("lblAdultConfirmYes"),
                _localizationService.GetString("keyNo"));
            if (confirmed)
            {
                AppSettings.Default.ShowAdultApps = true;
                AppSettings.Default.Save();
                ShowAdultAppsChanged?.Invoke();
                return;
            }
            _revertingShowAdultApps = true;
            try { ShowAdultApps = false; }
            finally { _revertingShowAdultApps = false; }
        }

        partial void OnDeletePreviousInstallChanged(bool value)
        {
            AppSettings.Default.DeletePreviousInstall = value;
            AppSettings.Default.Save();
        }

        partial void OnRtlReadingChanged(bool value)
        {
            AppSettings.Default.RTLReading = value;
            AppSettings.Default.Save();
        }

        partial void OnOpenAfterInstallChanged(bool value)
        {
            AppSettings.Default.OpenAfterInstall = value;
            AppSettings.Default.Save();
        }

        partial void OnKeepWGTFileChanged(bool value)
        {
            AppSettings.Default.KeepWGTFile = value;
            AppSettings.Default.Save();
        }

        partial void OnDarkModeChanged(bool value)
        {
            _themeService.SetTheme(value);
        }

        partial void OnManualDuidsChanged(string value)
        {
            AppSettings.Default.ManualDuids = value;
            AppSettings.Default.Save();
        }

        partial void OnGitHubTokenChanged(string value)
        {
            AppSettings.Default.GitHubToken = value;
            AppSettings.Default.Save();
        }

        partial void OnShowGitHubTokenChanged(bool value)
        {
            OnPropertyChanged(nameof(GitHubTokenPasswordChar));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
            AppSettings.PartnerSigningAutoEnabled -= OnPartnerSigningAutoEnabled;
        }
    }
}
