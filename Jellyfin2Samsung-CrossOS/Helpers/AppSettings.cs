using Jellyfin2Samsung.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin2Samsung.Helpers
{
    public class AppSettings
    {
        private const string FileName = "settings.json";
        public static readonly string FolderPath = AppContext.BaseDirectory;
        public static readonly string FilePath = Path.Combine(FolderPath, FileName);
        public static readonly string TizenSdbPath = Path.Combine(FolderPath, "Assets", "TizenSDB");
        public static readonly string CertificatePath = Path.Combine(FolderPath, "Assets", "Certificate");
        public static readonly string ProfilePath = Path.Combine(FolderPath, "Assets", "TizenProfile");
        public static readonly string EsbuildPath = Path.Combine(FolderPath, "Assets", "esbuild");
        public static readonly string DownloadPath = Path.Combine(FolderPath, "Downloads");

        private static AppSettings? _instance;

        // --- Runtime-only cached object (not saved to disk) ---
        [JsonIgnore]
        public ExistingCertificates? ChosenCertificates { get; set; }
        [JsonIgnore]
        public string CustomWgtPath { get; set; } = "";
        [JsonIgnore]
        public string LocalIp { get; set; } = "";
        [JsonIgnore]
        public string TvIp { get; set; } = "";
        public static AppSettings Default => _instance ??= Load();

        // ----- User-scoped settings -----
        public string Language { get; set; } = "en";
        public string Certificate { get; set; } = "Jelly2Sams";
        public bool DeletePreviousInstall { get; set; } = false;
        public string UserCustomIP { get; set; } = "";
        public string SavedNetworkInterfaceName { get; set; } = "";
        public bool ForceSamsungLogin { get; set; } = false;
        public bool RTLReading { get; set; } = false;
        public string JellyfinIP { get; set; } = "";
        public string JellyfinBasePath { get; set; } = "";
        public string ServerInputMode { get; set; } = "IP : Port";
        public string JellyfinUsername { get; set; } = "";
        public string JellyfinPassword { get; set; } = "";
        public string JellyfinAccessToken { get; set; } = "";
        public string JellyfinServerId { get; set; } = "";
        public string JellyfinServerLocalAddress { get; set; } = "";
        public string JellyfinServerName { get; set; } = "";
        public string AudioLanguagePreference { get; set; } = "";
        public string SubtitleLanguagePreference { get; set; } = "";
        public bool EnableBackdrops { get; set; } = false;
        public bool EnableThemeSongs { get; set; } = false;
        public bool EnableThemeVideos { get; set; } = false;
        public bool BackdropScreensaver { get; set; } = false;
        public bool DetailsBanner { get; set; } = false;
        public bool CinemaMode { get; set; } = false;
        public bool NextUpEnabled { get; set; } = false;
        public bool EnableExternalVideoPlayers { get; set; } = false;
        public bool SkipIntros { get; set; } = false;
        public string SelectedTheme { get; set; } = "dark";
        public string SelectedSubtitleMode { get; set; } = "Default";
        public string JellyfinUserId { get; set; } = "";
        public bool IsJellyfinAdmin { get; set; } = false;
        public string SelectedUserIds { get; set; } = "";  // Comma-separated list of selected user IDs for multi-user config
        public string DistributorsEndpoint_V1 { get; set; } = "https://svdca.samsungqbe.com/apis/v1/distributors";
        public string DistributorsEndpoint_V3 { get; set; } = "https://svdca.samsungqbe.com/apis/v3/distributors";
        public string AuthorEndpoint_V3 { get; set; } = "https://svdca.samsungqbe.com/apis/v3/authors";
        public bool TryOverwrite { get; set; } = true;
        public bool UseServerScripts { get; set; } = false;
        public string DisabledPluginIds { get; set; } = "";  // CSV of plugin Ids the user opted out of patching
        public bool OpenAfterInstall { get; set; } = false;
        public bool EnableDevLogs { get; set; } = false;
        public bool KeepWGTFile { get; set; } = false;
        public bool PatchYoutubePlugin { get; set; } = false;
        public string CustomCss { get; set; } = "";
        public bool DarkMode { get; set; } = false;
        public string GitHubToken { get; set; } = "";
        public string LocalYoutubeServer { get; set; } = string.Empty;

        // ----- Updater settings -----
        public bool CheckForUpdatesOnStartup { get; set; } = true;
        public string SkippedUpdateVersion { get; set; } = string.Empty;
        public DateTime? LastUpdateCheck { get; set; } = null;

        // ----- Application-scoped settings (readonly at runtime) -----
        public string AuthorEndpoint { get; set; } = "https://dev.tizen.samsung.com/apis/v2/authors";
        public string AppVersion { get; set; } = "v2.3.1.1";
        public string TizenSdb { get; set; } = "https://api.github.com/repos/PatrickSt1991/tizen-sdb/releases";
        public string JellyfinAvReleaseFork { get; set; } = "https://api.github.com/repos/asamahy/tizen-jellyfin-avplay/releases";
        public string ReleaseInfo { get; set; } = "https://raw.githubusercontent.com/jeppevinkel/jellyfin-tizen-builds/refs/heads/master/README.md";
        public string CommunityInfo { get; set; } = "https://raw.githubusercontent.com/PatrickSt1991/tizen-community-packages/refs/heads/main/README.md";
        public AppSettings() { }

        /// <summary>
        /// Gets the full Jellyfin URL including base path for reverse proxy setups.
        /// Example: https://xxx.seedhost.eu/xxx/jellyfin
        /// </summary>
        [JsonIgnore]
        public string JellyfinFullUrl
        {
            get
            {
                var baseUrl = Core.UrlHelper.NormalizeServerUrl(JellyfinIP);
                var basePath = JellyfinBasePath?.Trim('/') ?? "";

                if (string.IsNullOrEmpty(basePath))
                    return baseUrl;

                return $"{baseUrl}/{basePath}";
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Ignore errors for now
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                        _instance = settings;
                }
            }
            catch
            {
                // ignore load errors
            }

            return _instance ??= new AppSettings();
        }
    }
}
