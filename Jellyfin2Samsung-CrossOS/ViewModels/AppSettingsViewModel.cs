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
using Apps2Samsung.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        private readonly CertificateHelper _certificateHelper;
        private readonly INetworkService _networkService;
        private readonly IThemeService _themeService;
        private readonly FileHelper _fileHelper;
        private readonly ProviderManifestService _providerManifestService;

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

        [ObservableProperty]
        private bool showAllJellyfinVersions;

        [ObservableProperty]
        private bool rtlReading;

        [ObservableProperty]
        private bool openAfterInstall;

        [ObservableProperty]
        private bool keepWGTFile;

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
        public ObservableCollection<ExistingCertificates> AvailableCertificates { get; } = new();
        public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; } = new();
        public ObservableCollection<AppIconEntry> AppIcons { get; } = new();

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
        public string LblForceLogin => _localizationService.GetString("lblForceLogin");
        public string LblShowAllJellyfinVersions => _localizationService.GetString("lblShowAllJellyfinVersions");
        public string LblRTL => _localizationService.GetString("lblRTL");
        public string LblKeepWGTFile => _localizationService.GetString("lblKeepWGTFile");
        public string LblSettingsHeader => _localizationService.GetString("lblSettings");
        public string LblGitHubToken => _localizationService.GetString("lblGitHubToken");
        public string LblGitHubTokenHint => _localizationService.GetString("lblGitHubTokenHint");
        public string LblManualDuids => _localizationService.GetString("lblManualDuids");
        public string LblManualDuidsHint => _localizationService.GetString("lblManualDuidsHint");
        public string LblOpenLogsFolder => _localizationService.GetString("lblOpenLogsFolder");
        public string LblAppIcons => _localizationService.GetString("lblAppIcons");
        public string LblAppIconsHint => _localizationService.GetString("lblAppIconsHint");
        public string LblIconOblong => _localizationService.GetString("lblIconOblong");
        public string LblIconCustom => _localizationService.GetString("lblIconCustom");
        public string LblIconReset => _localizationService.GetString("lblIconReset");
        public string LblIconDefault => _localizationService.GetString("lblIconDefault");

        public AppSettingsViewModel(
            ILocalizationService localizationService,
            CertificateHelper certificateHelper,
            INetworkService networkService,
            IThemeService themeService,
            FileHelper fileHelper,
            HttpClient httpClient)
        {
            _localizationService = localizationService;
            _certificateHelper = certificateHelper;
            _networkService = networkService;
            _themeService = themeService;
            _fileHelper = fileHelper;
            _providerManifestService = new ProviderManifestService(httpClient);

            _localizationService.LanguageChanged += OnLanguageChanged;
            _themeService.ThemeChanged += OnThemeChanged;

            AvailableLanguages = new ObservableCollection<LanguageOption>(
                _localizationService.AvailableLanguages
                    .Select(code => new LanguageOption
                    {
                        Code = code,
                        Name = GetLanguageDisplayName(code)
                    })
                    .OrderBy(lang => lang.Name)
            );

            InitializeMainSettings();
            _ = LoadNetworkInterfacesAsync();
            _ = InitializeCertificatesAsync();
            _ = LoadAppIconsAsync();
        }

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
            OnPropertyChanged(nameof(LblShowAllJellyfinVersions));
            OnPropertyChanged(nameof(LblRTL));
            OnPropertyChanged(nameof(LblKeepWGTFile));
            OnPropertyChanged(nameof(LblSettingsHeader));
            OnPropertyChanged(nameof(LblGitHubToken));
            OnPropertyChanged(nameof(LblGitHubTokenHint));
            OnPropertyChanged(nameof(LblManualDuids));
            OnPropertyChanged(nameof(LblManualDuidsHint));
            OnPropertyChanged(nameof(LblOpenLogsFolder));
            OnPropertyChanged(nameof(LblAppIcons));
            OnPropertyChanged(nameof(LblAppIconsHint));
            OnPropertyChanged(nameof(LblIconOblong));
            OnPropertyChanged(nameof(LblIconCustom));
            OnPropertyChanged(nameof(LblIconReset));
            OnPropertyChanged(nameof(LblIconDefault));

            foreach (var entry in AppIcons)
                RefreshSummary(entry);
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
            ShowAllJellyfinVersions = AppSettings.Default.ShowAllJellyfinVersions;
            RtlReading = AppSettings.Default.RTLReading;
            LocalIP = AppSettings.Default.LocalIp ?? string.Empty;
            TryOverwrite = AppSettings.Default.TryOverwrite;
            OpenAfterInstall = AppSettings.Default.OpenAfterInstall;
            KeepWGTFile = AppSettings.Default.KeepWGTFile;
            DarkMode = AppSettings.Default.DarkMode;
            GitHubToken = AppSettings.Default.GitHubToken ?? string.Empty;
            ManualDuids = AppSettings.Default.ManualDuids ?? string.Empty;
        }

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
                    SelectedNetworkInterface =
                        (!string.IsNullOrEmpty(savedName)
                            ? NetworkInterfaces.FirstOrDefault(i => i.Name == savedName)
                            : null)
                        ?? (!string.IsNullOrEmpty(savedIp)
                            ? NetworkInterfaces.FirstOrDefault(i => i.IpAddress == savedIp)
                            : null)
                        ?? NetworkInterfaces.FirstOrDefault();
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
                foreach (var cert in certificates)
                    AvailableCertificates.Add(cert);

                var savedCertName = AppSettings.Default.Certificate;
                ExistingCertificates? selectedCert = null;

                if (!string.IsNullOrEmpty(savedCertName))
                {
                    selectedCert = AvailableCertificates
                        .FirstOrDefault(c => c.Name == savedCertName);
                }

                selectedCert ??= AvailableCertificates
                        .FirstOrDefault(c => c.Name == "Jelly2Sams");

                selectedCert ??= AvailableCertificates
                        .FirstOrDefault(c => c.Name == "Jelly2Sams (default)");

                selectedCert ??= AvailableCertificates.FirstOrDefault();

                if (selectedCert != null)
                    SelectedCertificate = selectedCert.Name;

                AppSettings.Default.ChosenCertificates = selectedCert;
            });
        }

        private static string GetLanguageDisplayName(string code)
        {
            try
            {
                var name = new System.Globalization.CultureInfo(code).NativeName;
                return string.IsNullOrEmpty(name) ? code : char.ToUpper(name[0]) + name.Substring(1);
            }
            catch
            {
                return code;
            }
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            try
            {
                var logFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
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

        partial void OnShowAllJellyfinVersionsChanged(bool value)
        {
            AppSettings.Default.ShowAllJellyfinVersions = value;
            AppSettings.Default.Save();
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

        // ----- App icons (per-app launcher icon: default / bundled oblong / custom PNG) -----

        private async System.Threading.Tasks.Task LoadAppIconsAsync()
        {
            try
            {
                var manifest = await _providerManifestService.GetAsync();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new List<AppIconEntry>();

                void Add(string display, string key)
                {
                    key = key?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(key) || !seen.Add(key))
                        return;

                    entries.Add(new AppIconEntry
                    {
                        DisplayName = string.IsNullOrWhiteSpace(display) ? key : display.Trim(),
                        Key = key,
                        HasOblong = HasBundledOblong(key),
                    });
                }

                foreach (var provider in manifest.Providers)
                {
                    if (provider.ExpandAssets)
                        continue; // the community bundle expands into CommunityApps below

                    // All Jellyfin builds share the "Jellyfin" file-name root, so collapse the
                    // variants (AVPlay, Legacy, …) into a single entry that matches any of them.
                    if (string.IsNullOrWhiteSpace(provider.DisplayName) ||
                        provider.DisplayName.StartsWith("Jellyfin", StringComparison.OrdinalIgnoreCase))
                        Add("Jellyfin", "Jellyfin");
                    else
                        Add(provider.DisplayName, provider.DisplayName);
                }

                foreach (var community in manifest.CommunityApps)
                    Add(community.MatchName, community.MatchName);

                var map = LoadIconMap();
                foreach (var entry in entries)
                {
                    entry.Value = map.TryGetValue(entry.Key, out var v) ? v : string.Empty;
                    RefreshSummary(entry);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppIcons.Clear();
                    foreach (var entry in entries)
                        AppIcons.Add(entry);
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AppIcons] Failed to build the app-icon list: {ex.Message}");
            }
        }

        [RelayCommand]
        private void UseOblongIcon(AppIconEntry? entry)
        {
            if (entry != null)
                SetIcon(entry, AppIconEntry.OblongValue);
        }

        [RelayCommand]
        private async System.Threading.Tasks.Task BrowseCustomIconAsync(AppIconEntry? entry)
        {
            if (entry == null)
                return;

            var storageProvider = GetActiveStorageProvider();
            if (storageProvider == null)
                return;

            var path = await _fileHelper.BrowseImageFileAsync(storageProvider);
            if (!string.IsNullOrEmpty(path))
                SetIcon(entry, path);
        }

        [RelayCommand]
        private void ResetIcon(AppIconEntry? entry)
        {
            if (entry != null)
                SetIcon(entry, string.Empty);
        }

        private void SetIcon(AppIconEntry entry, string value)
        {
            entry.Value = value;
            RefreshSummary(entry);

            var map = LoadIconMap();
            if (string.IsNullOrEmpty(value))
                map.Remove(entry.Key);
            else
                map[entry.Key] = value;

            AppSettings.Default.CustomAppIconsJson = JsonSerializer.Serialize(map);
            AppSettings.Default.Save();
        }

        private void RefreshSummary(AppIconEntry entry)
        {
            entry.Summary = entry.IsDefault
                ? LblIconDefault
                : entry.IsOblong
                    ? LblIconOblong
                    : Path.GetFileName(entry.Value);
        }

        private static bool HasBundledOblong(string key)
            => key.IndexOf("tvapp", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("litefin", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Dictionary<string, string> LoadIconMap()
        {
            var json = AppSettings.Default.CustomAppIconsJson;
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } parsed
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static IStorageProvider? GetActiveStorageProvider()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return (desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow)?.StorageProvider;

            return null;
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
        }
    }
}
