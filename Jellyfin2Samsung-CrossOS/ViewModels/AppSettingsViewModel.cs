using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

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

        public AppSettingsViewModel(
            ILocalizationService localizationService,
            CertificateHelper certificateHelper,
            INetworkService networkService,
            IThemeService themeService)
        {
            _localizationService = localizationService;
            _certificateHelper = certificateHelper;
            _networkService = networkService;
            _themeService = themeService;

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

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
        }
    }
}
