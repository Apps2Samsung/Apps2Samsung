using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin2Samsung.Helpers;
using Jellyfin2Samsung.Helpers.API;
using Jellyfin2Samsung.Helpers.Core;
using Jellyfin2Samsung.Helpers.Jellyfin.Plugins;
using Jellyfin2Samsung.Helpers.Tizen.Certificate;
using Jellyfin2Samsung.Interfaces;
using Jellyfin2Samsung.Models;
using Jellyfin2Samsung.Services;
using Jellyfin2Samsung.Views;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Jellyfin2Samsung.ViewModels
{
    public partial class JellyfinConfigViewModel : ViewModelBase
    {
        private readonly JellyfinApiClient _jellyfinApiClient;
        private readonly ILocalizationService _localizationService;
        private readonly CertificateHelper _certificateHelper;
        private readonly INetworkService _networkService;
        private readonly IThemeService _themeService;

        [ObservableProperty]
        private string? audioLanguagePreference;

        [ObservableProperty]
        private string? subtitleLanguagePreference;

        [ObservableProperty]
        private string? jellyfinServerIp;

        [ObservableProperty]
        private string? selectedTheme;

        [ObservableProperty]
        private JellyTheme? selectedJellyTheme;

        [ObservableProperty]
        private Bitmap? selectedJellyThemePreview;

        [ObservableProperty]
        private string? selectedSubtitleMode;

        [ObservableProperty]
        private string selectedJellyfinPort = string.Empty;

        [ObservableProperty]
        private string selectedJellyfinProtocol = string.Empty;

        [ObservableProperty]
        private string jellyfinBasePath = string.Empty;

        [ObservableProperty]
        private string jellyfinUsername = string.Empty;

        [ObservableProperty]
        private string jellyfinPassword = string.Empty;

        [ObservableProperty]
        private bool isAuthenticated = false;

        [ObservableProperty]
        private bool isJellyfinAdmin = false;

        [ObservableProperty]
        private string authenticationStatus = string.Empty;

        [ObservableProperty]
        private string serverConnectionStatus = string.Empty;

        [ObservableProperty]
        private bool serverValidated = false;

        [ObservableProperty]
        private bool streamValidated = false;


        [ObservableProperty]
        private bool enableBackdrops;

        [ObservableProperty]
        private bool enableThemeSongs;

        [ObservableProperty]
        private bool enableThemeVideos;

        [ObservableProperty]
        private bool backdropScreensaver;

        [ObservableProperty]
        private bool detailsBanner;

        [ObservableProperty]
        private bool cinemaMode;

        [ObservableProperty]
        private bool nextUpEnabled;

        [ObservableProperty]
        private bool enableExternalVideoPlayers;

        [ObservableProperty]
        private bool skipIntros;

        [ObservableProperty]
        private bool useServerScripts;

        [ObservableProperty]
        private bool serverIpSet = false;

        [ObservableProperty]
        private bool enableDevLogs = false;
        [ObservableProperty]
        private bool patchYoutubePlugin = false;

        [ObservableProperty]
        private string customCss = string.Empty;

        [ObservableProperty]
        private string cssValidationStatus = string.Empty;

        [ObservableProperty]
        private bool cssValidationSuccess = false;

        [ObservableProperty]
        private bool isValidatingCss = false;

        [ObservableProperty]
        private bool canOpenDebugWindow;

        [ObservableProperty]
        private bool showMdnsWarning = false;

        [ObservableProperty]
        private string selectedServerInputMode = "IP : Port";

        [ObservableProperty]
        private string jellyfinFullUrlInput = string.Empty;

        [ObservableProperty]
        private string localYoutubeServer = string.Empty;

        // ========== Main Settings Properties (from SettingsViewModel) ==========
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
        private NetworkInterfaceOption? selectedNetworkInterface;

        public ObservableCollection<LanguageOption> AvailableLanguages { get; }
        public ObservableCollection<ExistingCertificates> AvailableCertificates { get; } = new();
        public ObservableCollection<JellyfinUser> AvailableJellyfinUsers { get; } = new();
        public ObservableCollection<JellyfinUser> SelectedJellyfinUsers { get; }
        public ObservableCollection<InstalledPluginItem> InstalledPlugins { get; } = new();
        public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces { get; } = new();
        // ========== End Main Settings Properties ==========

        public ObservableCollection<string> AvailableThemes { get; } = new()
        {
            "appletv",
            "blueradiance",
            "dark",
            "light",
            "purplehaze",
            "wmc"
        };

        public ObservableCollection<string> AvailableSubtitleModes { get; } = new()
        {
            "None",
            "OnlyForced",
            "Default",
            "Always"
        };

        public ObservableCollection<string> JellyfinPorts { get; } = new()
        {
            "8096", "8920"
        };

        public ObservableCollection<string> AvailableServerInputModes { get; } = new()
        {
            "IP : Port",
            "IP : Port : Path",
            "Full URL"
        };

        // Computed properties for server input mode visibility
        public bool IsServerIpPortMode => SelectedServerInputMode == "IP : Port";
        public bool IsServerIpPortBasePathMode => SelectedServerInputMode == "IP : Port : Path";
        public bool IsServerFullUrlMode => SelectedServerInputMode == "Full URL";
        public bool HasSelectedJellyTheme => SelectedJellyTheme != null;
        public bool CanClearCss => !string.IsNullOrWhiteSpace(CustomCss);

        // Status color properties
        public IBrush ServerStatusColor => ServerValidated
            ? new SolidColorBrush(Color.FromRgb(39, 174, 96))   // Green
            : new SolidColorBrush(Color.FromRgb(127, 140, 141)); // Gray

        public IBrush AuthStatusColor => IsAuthenticated
            ? new SolidColorBrush(Color.FromRgb(39, 174, 96))   // Green
            : new SolidColorBrush(Color.FromRgb(127, 140, 141)); // Gray

        public IBrush CssValidationColor => CssValidationSuccess
            ? new SolidColorBrush(Color.FromRgb(39, 174, 96))   // Green
            : new SolidColorBrush(Color.FromRgb(231, 76, 60));  // Red

        public string LblYtDlpServer => _localizationService.GetString("lblYtDlpServer");
        public string LblJellyfinConfig => _localizationService.GetString("lblJellyfinConfig");
        public string LblServerSettings => _localizationService.GetString("lblServerSettings");
        public string ServerIP => _localizationService.GetString("ServerIP");
        public string LblEnableBackdrops => _localizationService.GetString("lblEnableBackdrops");
        public string LblEnableThemeSongs => _localizationService.GetString("lblEnableThemeSongs");
        public string LblEnableThemeVideos => _localizationService.GetString("lblEnableThemeVideos");
        public string LblBackdropScreensaver => _localizationService.GetString("lblBackdropScreensaver");
        public string LblDetailsBanner => _localizationService.GetString("lblDetailsBanner");
        public string LblCinemaMode => _localizationService.GetString("lblCinemaMode");
        public string LblNextUpEnabled => _localizationService.GetString("lblNextUpEnabled");
        public string LblEnableExternalVideoPlayers => _localizationService.GetString("lblEnableExternalVideoPlayers");
        public string LblSkipIntros => _localizationService.GetString("lblSkipIntros");
        public string LblAudioLanguagePreference => _localizationService.GetString("lblAudioLanguagePreference");
        public string LblSubtitleLanguagePreference => _localizationService.GetString("lblSubtitleLanguagePreference");
        public string Theme => _localizationService.GetString("Theme");
        public string LblSubtitleMode => _localizationService.GetString("lblSubtitleMode");
        public string LblBrowserSettings => _localizationService.GetString("lblBrowserSettings");
        public string LblUseServerScripts => _localizationService.GetString("lblUseServerScripts");
        public string LblEnableDevLogs => _localizationService.GetString("lblEnableDevLogs");
        public string lblOpenDebugWindow => _localizationService.GetString("lblOpenDebugWindow");
        public string lblFixYouTube153 => _localizationService.GetString("FixYouTube153");
        public string LblBasePath => _localizationService.GetString("lblBasePath");
        public string LblJellyfinUsername => _localizationService.GetString("lblJellyfinUsername");
        public string LblJellyfinPassword => _localizationService.GetString("lblJellyfinPassword");
        public string LblAuthenticate => _localizationService.GetString("lblAuthenticate");
        public string LblAutoLoginSettings => _localizationService.GetString("lblAutoLoginSettings");
        public string LblAdvancedSettings => _localizationService.GetString("lblAdvancedSettings");
        public string LblBasePathHint => _localizationService.GetString("lblBasePathHint");
        public string LblTestServer => _localizationService.GetString("lblTestServer");
        public string LblLogout => _localizationService.GetString("lblLogout");
        public string LblSelectUsers => _localizationService.GetString("lblSelectUsers");
        public string LblRefreshUsers => _localizationService.GetString("lblRefreshUsers");
        public string LblUserSelectionHint => _localizationService.GetString("lblUserSelectionHint");
        public string LblInstalledPlugins => _localizationService.GetString("lblInstalledPlugins");
        public string LblInstalledPluginsHint => _localizationService.GetString("lblInstalledPluginsHint");
        public string LblPluginUnsupported => _localizationService.GetString("lblPluginUnsupported");
        public string LblValidateStream => _localizationService.GetString("lblValidateStream");

        // New Tab and UI labels
        public string LblTabServer => _localizationService.GetString("lblTabServer");
        public string LblTabPlayback => _localizationService.GetString("lblTabPlayback");
        public string LblServerInputMode => _localizationService.GetString("lblServerInputMode");
        public string LblServerUrl => _localizationService.GetString("lblServerUrl");
        public string LblConnectionStatus => _localizationService.GetString("lblConnectionStatus");

        // CSS Tab labels
        public string LblTabCss => _localizationService.GetString("lblTabCss");
        public string LblCssSettings => _localizationService.GetString("lblCssSettings");
        public string LblCustomCss => _localizationService.GetString("lblCustomCss");
        public string LblCssHint => _localizationService.GetString("lblCssHint");
        public string LblValidateCss => _localizationService.GetString("lblValidateCss");
        public string LblCssValidationStatus => _localizationService.GetString("lblCssValidationStatus");
        public string LblClearCss => _localizationService.GetString("lblClearCss");
        public string LblMdnsWarning => _localizationService.GetString("lblMdnsWarning");


        // Main Settings Tab labels
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
        public string LblRTL => _localizationService.GetString("lblRTL");
        public string LblKeepWGTFile => _localizationService.GetString("lblKeepWGTFile");
        public string LblSettingsHeader => _localizationService.GetString("lblSettings");
        public string LblGitHubToken => _localizationService.GetString("lblGitHubToken");
        public string LblGitHubTokenHint => _localizationService.GetString("lblGitHubTokenHint");
        public string LblOpenLogsFolder => _localizationService.GetString("lblOpenLogsFolder");
        public string LblJellyfinOnlyNotice => _localizationService.GetString("lblJellyfinOnlyNotice");
        public char GitHubTokenPasswordChar => ShowGitHubToken ? '\0' : '*';

        // Password intentionally not required: Jellyfin allows passwordless accounts.
        // The server is the source of truth and rejects a blank password for protected accounts.
        public bool CanLogin => ServerValidated &&
                                !string.IsNullOrWhiteSpace(JellyfinUsername);

        public bool CanValidateCss => !string.IsNullOrWhiteSpace(CustomCss) && !IsValidatingCss;
        public string TvIp => AppSettings.Default.TvIp;

        public JellyfinConfigViewModel(
            JellyfinApiClient jellyfinApiClient,
            ILocalizationService localizationService,
            CertificateHelper certificateHelper,
            INetworkService networkService,
            IThemeService themeService)
        {
            _jellyfinApiClient = jellyfinApiClient;
            _localizationService = localizationService;
            _certificateHelper = certificateHelper;
            _networkService = networkService;
            _themeService = themeService;
            _localizationService.LanguageChanged += OnLanguageChanged;
            _themeService.ThemeChanged += OnThemeChanged;

            // Initialize selected users collection with change tracking
            SelectedJellyfinUsers = new ObservableCollection<JellyfinUser>();
            SelectedJellyfinUsers.CollectionChanged += OnSelectedJellyfinUsersChanged;

            // Initialize available languages collection
            AvailableLanguages = new ObservableCollection<LanguageOption>(
                _localizationService.AvailableLanguages
                    .Select(code => new LanguageOption
                    {
                        Code = code,
                        Name = GetLanguageDisplayName(code)
                    })
                    .OrderBy(lang => lang.Name)
            );

            InitializeAsyncSettings();
            InitializeMainSettings();
            UpdateServerIpStatus();
            _ = LoadNetworkInterfacesAsync();
            _ = InitializeCertificatesAsync();
        }

        private void OnSelectedJellyfinUsersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Save selected user IDs to AppSettings as comma-separated string
            var userIds = SelectedJellyfinUsers.Select(u => u.Id).ToArray();
            AppSettings.Default.SelectedUserIds = string.Join(",", userIds);
            AppSettings.Default.Save();
            Trace.WriteLine($"[SelectedUsers] Saved {userIds.Length} user IDs: {AppSettings.Default.SelectedUserIds}");
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
            OnPropertyChanged(nameof(LblYtDlpServer));
            OnPropertyChanged(nameof(LblJellyfinConfig));
            OnPropertyChanged(nameof(LblServerSettings));
            OnPropertyChanged(nameof(ServerIP));
            OnPropertyChanged(nameof(LblEnableBackdrops));
            OnPropertyChanged(nameof(LblEnableThemeSongs));
            OnPropertyChanged(nameof(LblEnableThemeVideos));
            OnPropertyChanged(nameof(LblBackdropScreensaver));
            OnPropertyChanged(nameof(LblDetailsBanner));
            OnPropertyChanged(nameof(LblCinemaMode));
            OnPropertyChanged(nameof(LblNextUpEnabled));
            OnPropertyChanged(nameof(LblEnableExternalVideoPlayers));
            OnPropertyChanged(nameof(LblSkipIntros));
            OnPropertyChanged(nameof(LblAudioLanguagePreference));
            OnPropertyChanged(nameof(LblSubtitleLanguagePreference));
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(LblSubtitleMode));
            OnPropertyChanged(nameof(LblBrowserSettings));
            OnPropertyChanged(nameof(LblUseServerScripts));
            OnPropertyChanged(nameof(LblEnableDevLogs));
            OnPropertyChanged(nameof(lblOpenDebugWindow));
            OnPropertyChanged(nameof(lblFixYouTube153));
            OnPropertyChanged(nameof(LblBasePath));
            OnPropertyChanged(nameof(LblJellyfinUsername));
            OnPropertyChanged(nameof(LblJellyfinPassword));
            OnPropertyChanged(nameof(LblAuthenticate));
            OnPropertyChanged(nameof(LblAutoLoginSettings));
            OnPropertyChanged(nameof(LblAdvancedSettings));
            OnPropertyChanged(nameof(LblBasePathHint));
            OnPropertyChanged(nameof(LblTestServer));
            OnPropertyChanged(nameof(LblLogout));
            OnPropertyChanged(nameof(LblSelectUsers));
            OnPropertyChanged(nameof(LblRefreshUsers));
            OnPropertyChanged(nameof(LblUserSelectionHint));
            OnPropertyChanged(nameof(LblInstalledPlugins));
            OnPropertyChanged(nameof(LblInstalledPluginsHint));
            OnPropertyChanged(nameof(LblPluginUnsupported));
            OnPropertyChanged(nameof(LblValidateStream));
            // New tab and UI labels
            OnPropertyChanged(nameof(LblTabServer));
            OnPropertyChanged(nameof(LblTabPlayback));
            OnPropertyChanged(nameof(LblServerInputMode));
            OnPropertyChanged(nameof(LblServerUrl));
            OnPropertyChanged(nameof(LblConnectionStatus));
            // CSS Tab labels
            OnPropertyChanged(nameof(LblTabCss));
            OnPropertyChanged(nameof(LblCssSettings));
            OnPropertyChanged(nameof(LblCustomCss));
            OnPropertyChanged(nameof(LblCssHint));
            OnPropertyChanged(nameof(LblValidateCss));
            OnPropertyChanged(nameof(LblCssValidationStatus));
            OnPropertyChanged(nameof(LblMdnsWarning));
            // Main Settings Tab labels
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
            OnPropertyChanged(nameof(LblRTL));
            OnPropertyChanged(nameof(LblKeepWGTFile));
            OnPropertyChanged(nameof(LblSettingsHeader));
            OnPropertyChanged(nameof(LblGitHubToken));
            OnPropertyChanged(nameof(LblGitHubTokenHint));
            OnPropertyChanged(nameof(LblOpenLogsFolder));
            OnPropertyChanged(nameof(LblJellyfinOnlyNotice));
        }

        partial void OnAudioLanguagePreferenceChanged(string? value)
        {
            AppSettings.Default.AudioLanguagePreference = value;
            AppSettings.Default.Save();
        }

        partial void OnSubtitleLanguagePreferenceChanged(string? value)
        {
            AppSettings.Default.SubtitleLanguagePreference = value;
            AppSettings.Default.Save();
        }

        partial void OnJellyfinServerIpChanged(string? value)
        {
            UpdateJellyfinAddress();
            ServerIpSet = !string.IsNullOrWhiteSpace(value);
        }

        partial void OnSelectedThemeChanged(string? value)
        {
            AppSettings.Default.SelectedTheme = value;
            AppSettings.Default.Save();
        }

        partial void OnSelectedSubtitleModeChanged(string? value)
        {
            AppSettings.Default.SelectedSubtitleMode = value;
            AppSettings.Default.Save();
        }

        partial void OnSelectedJellyfinPortChanged(string value)
        {
            UpdateJellyfinAddress();
        }

        partial void OnSelectedJellyfinProtocolChanged(string value)
        {
            UpdateJellyfinAddress();
        }

        partial void OnSelectedServerInputModeChanged(string value)
        {
            OnPropertyChanged(nameof(IsServerIpPortMode));
            OnPropertyChanged(nameof(IsServerIpPortBasePathMode));
            OnPropertyChanged(nameof(IsServerFullUrlMode));

            // Save the selected mode to persist across restarts
            AppSettings.Default.ServerInputMode = value;
            AppSettings.Default.Save();
        }

        partial void OnSelectedJellyThemeChanged(JellyTheme? value)
        {
            OnPropertyChanged(nameof(HasSelectedJellyTheme));
        }

        partial void OnLocalYoutubeServerChanged(string value)
        {
            AppSettings.Default.LocalYoutubeServer = value;
            AppSettings.Default.Save();
        }

        partial void OnJellyfinFullUrlInputChanged(string value)
        {
            // Parse the full URL and update all fields
            if (!string.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                SelectedJellyfinProtocol = uri.Scheme;
                JellyfinServerIp = uri.Host;
                SelectedJellyfinPort = uri.IsDefaultPort ? (uri.Scheme == "https" ? "443" : "80") : uri.Port.ToString();

                var path = UrlHelper.NormalizeServerUrl(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(path) && path != "/")
                {
                    jellyfinBasePath = path;
                    AppSettings.Default.JellyfinBasePath = path;
                }
                else
                {
                    jellyfinBasePath = "";
                    AppSettings.Default.JellyfinBasePath = "";
                }
                OnPropertyChanged(nameof(JellyfinBasePath));
                AppSettings.Default.Save();

                CheckForMdnsHostname(uri.Host);

                // Auto-validate the server connection
                _ = AutoValidateServerAsync();
            }
        }

        partial void OnJellyfinBasePathChanged(string value)
        {
            // Check if user pasted a full URL - auto-parse it
            if (!string.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                // User pasted a full URL like https://xx.seedhost.eu/xx/jellyfin
                SelectedJellyfinProtocol = uri.Scheme;
                JellyfinServerIp = uri.Host;
                SelectedJellyfinPort = uri.IsDefaultPort ? (uri.Scheme == "https" ? "443" : "80") : uri.Port.ToString();

                // Extract the path portion (without triggering this handler again)
                var path = UrlHelper.NormalizeServerUrl(uri.AbsolutePath);
                AppSettings.Default.JellyfinBasePath = path;
                jellyfinBasePath = path; // Set backing field directly to avoid recursion
                OnPropertyChanged(nameof(JellyfinBasePath));
            }
            else
            {
                AppSettings.Default.JellyfinBasePath = value;
            }
            AppSettings.Default.Save();
            UpdateServerIpStatus();
        }

        partial void OnJellyfinUsernameChanged(string value)
        {
            AppSettings.Default.JellyfinUsername = value;
            AppSettings.Default.Save();
            OnPropertyChanged(nameof(CanLogin));
        }

        partial void OnServerValidatedChanged(bool value)
        {
            OnPropertyChanged(nameof(ServerStatusColor));
            OnPropertyChanged(nameof(CanLogin));
        }

        partial void OnIsAuthenticatedChanged(bool value)
        {
            OnPropertyChanged(nameof(AuthStatusColor));
            LogoutCommand.NotifyCanExecuteChanged();
            RefreshUsersCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsJellyfinAdminChanged(bool value)
        {
            RefreshUsersCommand.NotifyCanExecuteChanged();
        }

        partial void OnJellyfinPasswordChanged(string value)
        {
            AppSettings.Default.JellyfinPassword = value;
            AppSettings.Default.Save();
            OnPropertyChanged(nameof(CanLogin));
        }

        [RelayCommand]
        private async Task AuthenticateAsync()
        {
            // Only the username is required. A blank password is valid for passwordless
            // Jellyfin accounts; the server rejects it for password-protected accounts.
            if (string.IsNullOrWhiteSpace(JellyfinUsername))
            {
                AuthenticationStatus = "Enter username";
                return;
            }

            AuthenticationStatus = "Authenticating...";

            var (accessToken, userId, isAdmin, error) = await _jellyfinApiClient.AuthenticateAsync(JellyfinUsername, JellyfinPassword);

            if (accessToken != null && userId != null)
            {
                AppSettings.Default.JellyfinAccessToken = accessToken;
                AppSettings.Default.JellyfinUserId = userId;
                AppSettings.Default.JellyfinUsername = JellyfinUsername;
                AppSettings.Default.IsJellyfinAdmin = isAdmin;

                // Fetch and store the real server ID for auto-login compatibility
                await FetchAndStoreServerIdAsync();

                AppSettings.Default.Save();

                IsAuthenticated = true;
                IsJellyfinAdmin = isAdmin;
                AuthenticationStatus = isAdmin ? "Authenticated (Admin)" : "Authenticated";

                // If admin, load all Jellyfin users for multi-user selection
                if (isAdmin)
                {
                    await LoadJellyfinUsersAsync();
                }

                await LoadInstalledPluginsAsync();
            }
            else
            {
                IsAuthenticated = false;
                IsJellyfinAdmin = false;
                AuthenticationStatus = error ?? "Failed";
            }
        }

        /// <summary>
        /// Pulls the server's installed-plugin list, marks each as supported (we have a patch)
        /// or unsupported, and pre-selects everything that wasn't previously opted out.
        /// </summary>
        private async Task LoadInstalledPluginsAsync()
        {
            try
            {
                var serverUrl = UrlHelper.NormalizeServerUrl(AppSettings.Default.JellyfinFullUrl);
                var plugins = await _jellyfinApiClient.GetInstalledPluginsAsync(serverUrl);

                var disabled = (AppSettings.Default.DisabledPluginIds ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var existing in InstalledPlugins)
                        existing.PropertyChanged -= OnInstalledPluginChanged;
                    InstalledPlugins.Clear();

                    foreach (var p in plugins.OrderBy(p => p.Name))
                    {
                        var supported = PluginManager.IsSupported(p);
                        var item = new InstalledPluginItem
                        {
                            Id = p.Id ?? "",
                            Name = p.Name ?? "",
                            Version = p.Version,
                            IsSupported = supported,
                            // Default supported plugins to selected; respect a previous opt-out.
                            IsSelected = supported && !disabled.Contains(p.Id ?? "")
                        };
                        item.PropertyChanged += OnInstalledPluginChanged;
                        InstalledPlugins.Add(item);
                    }
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LoadInstalledPlugins] Error: {ex}");
            }
        }

        private void OnInstalledPluginChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(InstalledPluginItem.IsSelected)) return;

            var disabledIds = InstalledPlugins
                .Where(p => p.IsSupported && !p.IsSelected && !string.IsNullOrEmpty(p.Id))
                .Select(p => p.Id);

            AppSettings.Default.DisabledPluginIds = string.Join(",", disabledIds);
            AppSettings.Default.Save();
        }

        /// <summary>
        /// Loads all Jellyfin users when admin is authenticated.
        /// </summary>
        private async Task LoadJellyfinUsersAsync()
        {
            try
            {
                var users = await _jellyfinApiClient.LoadUsersAsync();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AvailableJellyfinUsers.Clear();
                    SelectedJellyfinUsers.Clear();

                    foreach (var user in users)
                    {
                        AvailableJellyfinUsers.Add(user);
                    }

                    // Pre-select the currently authenticated user
                    var currentUser = AvailableJellyfinUsers.FirstOrDefault(u => u.Id == AppSettings.Default.JellyfinUserId);
                    if (currentUser != null)
                    {
                        SelectedJellyfinUsers.Add(currentUser);
                    }
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[LoadUsers] Error: {ex}");
            }
        }

        [RelayCommand(CanExecute = nameof(IsAuthenticated))]
        private void Logout()
        {
            // Clear all authentication data
            AppSettings.Default.JellyfinAccessToken = "";
            AppSettings.Default.JellyfinUserId = "";
            AppSettings.Default.JellyfinServerId = "";
            AppSettings.Default.JellyfinServerLocalAddress = "";
            AppSettings.Default.JellyfinServerName = "";
            AppSettings.Default.IsJellyfinAdmin = false;
            AppSettings.Default.Save();

            // Reset UI state
            IsAuthenticated = false;
            IsJellyfinAdmin = false;
            AuthenticationStatus = "Logged out";

            // Clear user collections
            AvailableJellyfinUsers.Clear();
            SelectedJellyfinUsers.Clear();

            // Clear plugin inventory
            foreach (var p in InstalledPlugins)
                p.PropertyChanged -= OnInstalledPluginChanged;
            InstalledPlugins.Clear();

            // Notify command state changed
            LogoutCommand.NotifyCanExecuteChanged();
            RefreshUsersCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand(CanExecute = nameof(IsJellyfinAdmin))]
        private async Task RefreshUsersAsync()
        {
            await LoadJellyfinUsersAsync();
        }

        [RelayCommand]
        private async Task TestServerAsync()
        {
            ServerConnectionStatus = "Testing...";
            ServerValidated = false;
            OnPropertyChanged(nameof(CanLogin));

            var testUrl = UrlHelper.CombineUrl(AppSettings.Default.JellyfinFullUrl, "/System/Info/Public");
            var isReachable = await _jellyfinApiClient.TestServerConnectionAsync(testUrl);

            if (isReachable)
            {
                // Fetch and store the real server ID for auto-login compatibility
                await FetchAndStoreServerIdAsync();

                ServerConnectionStatus = "Server OK!";
                ServerValidated = true;
            }
            else
            {
                ServerConnectionStatus = "Connection failed!";
                ServerValidated = false;
            }
            OnPropertyChanged(nameof(CanLogin));
        }

        /// <summary>
        /// Fetches the real Jellyfin server ID from /System/Info/Public and stores it in AppSettings.
        /// This is required for auto-login to work correctly - Jellyfin compares the stored server ID
        /// against the actual server ID and returns ServerMismatch if they don't match.
        /// </summary>
        private async Task FetchAndStoreServerIdAsync()
        {
            try
            {
                var serverInfo = await _jellyfinApiClient.GetPublicSystemInfoAsync(AppSettings.Default.JellyfinFullUrl);
                if (serverInfo != null)
                {
                    if (!string.IsNullOrEmpty(serverInfo.Id))
                    {
                        AppSettings.Default.JellyfinServerId = serverInfo.Id;
                        Trace.WriteLine($"[ServerID] Stored real server ID: {serverInfo.Id}");
                    }

                    if (!string.IsNullOrEmpty(serverInfo.LocalAddress))
                    {
                        AppSettings.Default.JellyfinServerLocalAddress = serverInfo.LocalAddress;
                        Trace.WriteLine($"[ServerID] Stored server LocalAddress: {serverInfo.LocalAddress}");
                    }

                    if (!string.IsNullOrEmpty(serverInfo.ServerName))
                    {
                        AppSettings.Default.JellyfinServerName = serverInfo.ServerName;
                        Trace.WriteLine($"[ServerID] Stored server name: {serverInfo.ServerName}");
                    }

                    AppSettings.Default.Save();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ServerID] Failed to fetch server ID: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-validates the server connection when URL changes.
        /// Called automatically when server settings change.
        /// </summary>
        private async Task AutoValidateServerAsync()
        {
            if (!ServerIpSet)
            {
                ServerConnectionStatus = "Not configured";
                ServerValidated = false;
                return;
            }

            ServerConnectionStatus = "Validating...";
            ServerValidated = false;

            var testUrl = UrlHelper.CombineUrl(AppSettings.Default.JellyfinFullUrl, "/System/Info/Public");
            var isReachable = await _jellyfinApiClient.TestServerConnectionAsync(testUrl);

            if (isReachable)
            {
                await FetchAndStoreServerIdAsync();
                ServerConnectionStatus = "Connected";
                ServerValidated = true;
            }
            else
            {
                ServerConnectionStatus = "Unreachable";
                ServerValidated = false;
            }
        }

        partial void OnEnableBackdropsChanged(bool value)
        {
            AppSettings.Default.EnableBackdrops = value;
            AppSettings.Default.Save();
        }

        partial void OnEnableThemeSongsChanged(bool value)
        {
            AppSettings.Default.EnableThemeSongs = value;
            AppSettings.Default.Save();
        }

        partial void OnEnableThemeVideosChanged(bool value)
        {
            AppSettings.Default.EnableThemeVideos = value;
            AppSettings.Default.Save();
        }

        partial void OnBackdropScreensaverChanged(bool value)
        {
            AppSettings.Default.BackdropScreensaver = value;
            AppSettings.Default.Save();
        }

        partial void OnDetailsBannerChanged(bool value)
        {
            AppSettings.Default.DetailsBanner = value;
            AppSettings.Default.Save();
        }

        partial void OnCinemaModeChanged(bool value)
        {
            AppSettings.Default.CinemaMode = value;
            AppSettings.Default.Save();
        }

        partial void OnNextUpEnabledChanged(bool value)
        {
            AppSettings.Default.NextUpEnabled = value;
            AppSettings.Default.Save();
        }

        partial void OnEnableExternalVideoPlayersChanged(bool value)
        {
            AppSettings.Default.EnableExternalVideoPlayers = value;
            AppSettings.Default.Save();
        }

        partial void OnSkipIntrosChanged(bool value)
        {
            AppSettings.Default.SkipIntros = value;
            AppSettings.Default.Save();
        }

        partial void OnUseServerScriptsChanged(bool value)
        {
            AppSettings.Default.UseServerScripts = value;
            AppSettings.Default.Save();
        }

        partial void OnEnableDevLogsChanged(bool value)
        {
            AppSettings.Default.EnableDevLogs = value;
            AppSettings.Default.Save();

            CanOpenDebugWindow = (!string.IsNullOrWhiteSpace(TvIp)) && value;
            OpenDebugWindowCommand.NotifyCanExecuteChanged();
        }
        partial void OnPatchYoutubePluginChanged(bool value)
        {
            AppSettings.Default.PatchYoutubePlugin = value;
            AppSettings.Default.Save();
        }

        partial void OnCustomCssChanged(string value)
        {
            AppSettings.Default.CustomCss = value;
            AppSettings.Default.Save();
            // Reset validation status when CSS changes
            CssValidationStatus = string.Empty;
            CssValidationSuccess = false;
            OnPropertyChanged(nameof(CanValidateCss));
            OnPropertyChanged(nameof(CanClearCss));
        }

        partial void OnIsValidatingCssChanged(bool value)
        {
            OnPropertyChanged(nameof(CanValidateCss));
        }

        partial void OnCssValidationSuccessChanged(bool value)
        {
            OnPropertyChanged(nameof(CssValidationColor));
        }

        [RelayCommand]
        private async Task ValidateCssAsync()
        {
            if (string.IsNullOrWhiteSpace(CustomCss))
            {
                CssValidationStatus = _localizationService.GetString("lblCssEmpty");
                CssValidationSuccess = false;
                return;
            }

            IsValidatingCss = true;
            CssValidationStatus = _localizationService.GetString("lblCssValidating");

            try
            {
                // Extract @import URLs
                var importUrls = ExtractImportUrls(CustomCss);
                var failedUrls = new System.Collections.Generic.List<string>();

                // Test each URL
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                foreach (var url in importUrls)
                {
                    try
                    {
                        var response = await httpClient.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url));
                        if (!response.IsSuccessStatusCode)
                        {
                            // Try GET if HEAD fails (some servers don't support HEAD)
                            response = await httpClient.GetAsync(url);
                            if (!response.IsSuccessStatusCode)
                            {
                                failedUrls.Add(url);
                            }
                        }
                    }
                    catch
                    {
                        failedUrls.Add(url);
                    }
                }

                // Basic CSS syntax validation
                var syntaxErrors = ValidateCssSyntax(CustomCss);

                if (failedUrls.Count > 0)
                {
                    CssValidationStatus = string.Format(_localizationService.GetString("lblCssUrlFailed"), failedUrls.Count);
                    CssValidationSuccess = false;
                }
                else if (!string.IsNullOrEmpty(syntaxErrors))
                {
                    CssValidationStatus = syntaxErrors;
                    CssValidationSuccess = false;
                }
                else if (importUrls.Count > 0)
                {
                    CssValidationStatus = string.Format(_localizationService.GetString("lblCssUrlsValid"), importUrls.Count);
                    CssValidationSuccess = true;
                }
                else
                {
                    CssValidationStatus = _localizationService.GetString("lblCssSyntaxValid");
                    CssValidationSuccess = true;
                }
            }
            catch (Exception ex)
            {
                CssValidationStatus = $"Error: {ex.Message}";
                CssValidationSuccess = false;
            }
            finally
            {
                IsValidatingCss = false;
            }
        }
        private static async Task<Bitmap?> LoadPreviewAsync(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(url);
                using var ms = new System.IO.MemoryStream(bytes);
                return new Bitmap(ms);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ThemePreview] Failed to load preview: {ex.Message}");
                using var http = new HttpClient();
                var stream = await http.GetStreamAsync(url);
                return new Bitmap(stream);
            }
        }

        private System.Collections.Generic.List<string> ExtractImportUrls(string css)
        {
            var urls = new System.Collections.Generic.List<string>();
            // Match @import url("...") or @import url('...') or @import "..." or @import '...'
            var regex = new System.Text.RegularExpressions.Regex(
                @"@import\s+(?:url\s*\(\s*[""']?([^""')]+)[""']?\s*\)|[""']([^""']+)[""'])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in regex.Matches(css))
            {
                var url = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    urls.Add(url.Trim());
                }
            }
            return urls;
        }

        private string? ValidateCssSyntax(string css)
        {
            // Basic syntax validation - check for balanced braces
            int braceCount = 0;
            int parenCount = 0;

            foreach (char c in css)
            {
                switch (c)
                {
                    case '{': braceCount++; break;
                    case '}': braceCount--; break;
                    case '(': parenCount++; break;
                    case ')': parenCount--; break;
                }

                if (braceCount < 0)
                    return _localizationService.GetString("lblCssUnmatchedBrace");
                if (parenCount < 0)
                    return _localizationService.GetString("lblCssUnmatchedParen");
            }

            if (braceCount != 0)
                return _localizationService.GetString("lblCssUnmatchedBrace");
            if (parenCount != 0)
                return _localizationService.GetString("lblCssUnmatchedParen");

            return null;
        }

        #region JellyThemes

        /// <summary>
        /// Available JellyThemes from https://github.com/kingchenc/JellyThemes
        /// </summary>
        public static ObservableCollection<JellyTheme> JellyThemes { get; } = new()
        {
            new JellyTheme
            {
                Name = "Obsidian",
                Icon = "\U0001F7E3", // Purple circle
                ColorName = "Purple",
                HexColor = "#6B5B95",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Obsidian/Obsidian.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Obsidian/assets/preview/Obsidian.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Obsidian"
            },
            new JellyTheme
            {
                Name = "Solaris",
                Icon = "\U0001F7E1", // Yellow circle
                ColorName = "Gold",
                HexColor = "#D4AF37",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Solaris/Solaris.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Solaris/assets/preview/Solaris.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Solaris"
            },
            new JellyTheme
            {
                Name = "Nebula",
                Icon = "\U0001F535", // Blue circle
                ColorName = "Cyan",
                HexColor = "#00CED1",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Nebula/Nebula.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Nebula/assets/preview/Nebula.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Nebula"
            },
            new JellyTheme
            {
                Name = "Ember",
                Icon = "\U0001F7E0", // Orange circle
                ColorName = "Orange",
                HexColor = "#FF6B35",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Ember/Ember.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Ember/assets/preview/Ember.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Ember"
            },
            new JellyTheme
            {
                Name = "Void",
                Icon = "\u26AB", // Black circle
                ColorName = "Black",
                HexColor = "#1C1C1C",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Void/Void.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Void/assets/preview/Void.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Void"
            },
            new JellyTheme
            {
                Name = "Phantom",
                Icon = "\U0001F47B", // Ghost
                ColorName = "Slate",
                HexColor = "#708090",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Phantom/Phantom.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Phantom/assets/preview/Phantom.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Phantom"
            }
        };

        public string LblJellyThemes => _localizationService.GetString("lblJellyThemes");
        public string LblJellyThemesHint => _localizationService.GetString("lblJellyThemesHint");
        public const string JellyThemesRepoUrl = "https://github.com/kingchenc/JellyThemes";

        [RelayCommand]
        private async Task InsertThemeAsync(JellyTheme theme)
        {
            if (theme == null) return;

            SelectedJellyTheme = theme;
            CustomCss = theme.CssImportStatement;
            SelectedJellyThemePreview = await LoadPreviewAsync(theme.PreviewUrl);

            await ValidateCssAsync();
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

        [RelayCommand]
        private void OpenJellyThemesRepo()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = JellyThemesRepoUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open JellyThemes repo: {ex}");
            }
        }

        [RelayCommand]
        private void OpenThemeReadme(JellyTheme? theme)
        {
            if (theme == null || string.IsNullOrEmpty(theme.ReadmeUrl)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = theme.ReadmeUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to open theme readme: {ex}");
            }
        }
        [RelayCommand]
        private void ClearCss()
        {
            CustomCss = string.Empty;
            CssValidationStatus = string.Empty;
            CssValidationSuccess = false;
            SelectedJellyTheme = null;
            SelectedJellyThemePreview = null;
        }

        #endregion

        [RelayCommand(CanExecute = nameof(CanOpenDebugWindow))]
        private void OpenDebugWindow()
        {
            if (string.IsNullOrWhiteSpace(TvIp))
                return;

            // create VM with IP from settings
            var logService = App.Services.GetRequiredService<TvLogService>();
            var vm = new TvLogsViewModel(logService, TvIp, _localizationService);

            var window = new TvLogsWindow
            {
                DataContext = vm
            };

            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                window.Show(desktop.MainWindow);
            }
            else
            {
                window.Show();
            }
        }
        private async void InitializeAsyncSettings()
        {
            var jellyfinIP = AppSettings.Default.JellyfinIP;

            if (!string.IsNullOrWhiteSpace(jellyfinIP) && Uri.TryCreate(jellyfinIP, UriKind.Absolute, out var uri))
            {
                SelectedJellyfinProtocol = uri.Scheme;
                JellyfinServerIp = uri.Host;
                SelectedJellyfinPort = uri.Port.ToString();
                CheckForMdnsHostname(uri.Host);
            }
            else
            {
                SelectedJellyfinProtocol = "http";
                JellyfinServerIp = "";
                SelectedJellyfinPort = "8096";
            }

            // Load base path for reverse proxy support
            JellyfinBasePath = AppSettings.Default.JellyfinBasePath ?? "";

            // Load saved server input mode (default to "IP : Port" if not set)
            var savedMode = AppSettings.Default.ServerInputMode;
            if (!string.IsNullOrEmpty(savedMode) && AvailableServerInputModes.Contains(savedMode))
            {
                selectedServerInputMode = savedMode;
                OnPropertyChanged(nameof(SelectedServerInputMode));
                OnPropertyChanged(nameof(IsServerIpPortMode));
                OnPropertyChanged(nameof(IsServerIpPortBasePathMode));
                OnPropertyChanged(nameof(IsServerFullUrlMode));
            }

            // Populate JellyfinFullUrlInput from saved settings
            var fullUrl = AppSettings.Default.JellyfinFullUrl;
            if (!string.IsNullOrEmpty(fullUrl))
            {
                jellyfinFullUrlInput = fullUrl;
                OnPropertyChanged(nameof(JellyfinFullUrlInput));
            }

            // Load auto-login credentials
            JellyfinUsername = AppSettings.Default.JellyfinUsername ?? "";
            JellyfinPassword = AppSettings.Default.JellyfinPassword ?? "";
            IsAuthenticated = !string.IsNullOrEmpty(AppSettings.Default.JellyfinAccessToken);
            IsJellyfinAdmin = AppSettings.Default.IsJellyfinAdmin;
            if (IsAuthenticated)
            {
                AuthenticationStatus = IsJellyfinAdmin ? "Previously authenticated (Admin)" : "Previously authenticated";

                // If admin was previously authenticated, load users
                if (IsJellyfinAdmin)
                {
                    _ = LoadJellyfinUsersAsync();
                }

                _ = LoadInstalledPluginsAsync();
            }

            SelectedTheme = AppSettings.Default.SelectedTheme ?? "dark";
            SelectedSubtitleMode = AppSettings.Default.SelectedSubtitleMode ?? "None";
            AudioLanguagePreference = AppSettings.Default.AudioLanguagePreference ?? "";
            SubtitleLanguagePreference = AppSettings.Default.SubtitleLanguagePreference ?? "";

            EnableBackdrops = AppSettings.Default.EnableBackdrops;
            EnableThemeSongs = AppSettings.Default.EnableThemeSongs;
            EnableThemeVideos = AppSettings.Default.EnableThemeVideos;
            BackdropScreensaver = AppSettings.Default.BackdropScreensaver;
            DetailsBanner = AppSettings.Default.DetailsBanner;
            CinemaMode = AppSettings.Default.CinemaMode;
            NextUpEnabled = AppSettings.Default.NextUpEnabled;
            EnableExternalVideoPlayers = AppSettings.Default.EnableExternalVideoPlayers;
            SkipIntros = AppSettings.Default.SkipIntros;
            UseServerScripts = AppSettings.Default.UseServerScripts;
            EnableDevLogs = AppSettings.Default.EnableDevLogs;
            PatchYoutubePlugin = AppSettings.Default.PatchYoutubePlugin;
            CustomCss = AppSettings.Default.CustomCss ?? string.Empty;

            LocalYoutubeServer = AppSettings.Default.LocalYoutubeServer;

            CanOpenDebugWindow = EnableDevLogs && !string.IsNullOrWhiteSpace(fullUrl);
            OpenDebugWindowCommand.NotifyCanExecuteChanged();
        }

        private void UpdateJellyfinAddress()
        {
            if (!string.IsNullOrWhiteSpace(JellyfinServerIp) &&
                !string.IsNullOrWhiteSpace(SelectedJellyfinPort) &&
                !string.IsNullOrWhiteSpace(SelectedJellyfinProtocol))
            {
                // Only include port if it's not the default for the protocol
                var isDefaultPort = (SelectedJellyfinProtocol == "https" && SelectedJellyfinPort == "443") ||
                                    (SelectedJellyfinProtocol == "http" && SelectedJellyfinPort == "80");

                AppSettings.Default.JellyfinIP = isDefaultPort
                    ? $"{SelectedJellyfinProtocol}://{JellyfinServerIp}"
                    : $"{SelectedJellyfinProtocol}://{JellyfinServerIp}:{SelectedJellyfinPort}";

                Trace.WriteLine($"Updated Jellyfin IP: {AppSettings.Default.JellyfinIP}");
                AppSettings.Default.Save();
                UpdateServerIpStatus();
                CheckForMdnsHostname(JellyfinServerIp);
            }
        }

        /// <summary>
        /// Checks if the given hostname is an mDNS (.local) address and shows a warning.
        /// Samsung TVs (Tizen) cannot reliably resolve mDNS hostnames, which causes
        /// the server to appear as "undefined" on the TV after network disruptions.
        /// </summary>
        private void CheckForMdnsHostname(string? hostname)
        {
            ShowMdnsWarning = !string.IsNullOrEmpty(hostname) &&
                              hostname.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateServerIpStatus()
        {
            ServerIpSet = !string.IsNullOrEmpty(AppSettings.Default.JellyfinIP) ||
                          !string.IsNullOrWhiteSpace(JellyfinServerIp);
        }

        public void OnTvIpChanged()
        {
            OnPropertyChanged(nameof(TvIp));

            CanOpenDebugWindow = (!string.IsNullOrWhiteSpace(TvIp)) && EnableDevLogs;
            OpenDebugWindowCommand.NotifyCanExecuteChanged();
        }

        // ========== Main Settings Methods ==========

        private void InitializeMainSettings()
        {
            // Use current language from LocalizationService or fallback to saved setting
            var currentLangCode = _localizationService.CurrentLanguage ?? AppSettings.Default.Language ?? "en";

            SelectedLanguage = AvailableLanguages
                .FirstOrDefault(lang => string.Equals(lang.Code, currentLangCode, StringComparison.OrdinalIgnoreCase))
                ?? AvailableLanguages.FirstOrDefault();

            DeletePreviousInstall = AppSettings.Default.DeletePreviousInstall;
            ForceSamsungLogin = AppSettings.Default.ForceSamsungLogin;
            RtlReading = AppSettings.Default.RTLReading;
            LocalIP = AppSettings.Default.LocalIp ?? string.Empty;
            TryOverwrite = AppSettings.Default.TryOverwrite;
            OpenAfterInstall = AppSettings.Default.OpenAfterInstall;
            KeepWGTFile = AppSettings.Default.KeepWGTFile;
            DarkMode = AppSettings.Default.DarkMode;
            GitHubToken = AppSettings.Default.GitHubToken ?? string.Empty;
        }

        private async Task LoadNetworkInterfacesAsync()
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


        private async Task InitializeCertificatesAsync()
        {
            var certificates = _certificateHelper.GetAvailableCertificates(AppSettings.CertificatePath);

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

        partial void OnSelectedNetworkInterfaceChanged(NetworkInterfaceOption? value)
        {
            if (value == null)
                return;

            LocalIP = value.IpAddress;
            AppSettings.Default.LocalIp = value.IpAddress;
            AppSettings.Default.SavedNetworkInterfaceName = value.Name;
            AppSettings.Default.Save();
        }


        // Property changed handlers for Main Settings
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

        partial void OnGitHubTokenChanged(string value)
        {
            AppSettings.Default.GitHubToken = value;
            AppSettings.Default.Save();
        }

        partial void OnShowGitHubTokenChanged(bool value)
        {
            OnPropertyChanged(nameof(GitHubTokenPasswordChar));
        }

        // ========== End Main Settings Methods ==========

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _themeService.ThemeChanged -= OnThemeChanged;
        }
    }
}