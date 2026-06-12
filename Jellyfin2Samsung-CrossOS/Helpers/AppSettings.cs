using Apps2Samsung.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Helpers
{
    public class AppSettings
    {
        private const string FileName = "settings.json";

        // Shipped assets and regenerable caches live next to the binary (inside the .app bundle on macOS).
        public static readonly string FolderPath = AppContext.BaseDirectory;

        // User settings live in the per-user OS data directory so they survive app updates that
        // replace the install folder/bundle (e.g. a macOS .dmg reinstall).
        //   Windows: %APPDATA%\Apps2Samsung   macOS/Linux: ~/.config/Apps2Samsung
        public static readonly string DataFolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "Apps2Samsung");
        public static readonly string FilePath = Path.Combine(DataFolderPath, FileName);

        // Pre-2.5.1 location: next to the binary. Used once to migrate existing users' settings.
        private static readonly string LegacyFilePath = Path.Combine(FolderPath, FileName);
        public static readonly string TizenSdbPath = Path.Combine(FolderPath, "Assets", "TizenSDB");

        // Generated signing certificates live in the per-user data dir so they survive app/bundle
        // updates. A macOS .dmg (or an installer) replaces the whole install folder/bundle; if the
        // author cert lived there it would be wiped, a new one generated, and already-installed apps
        // could no longer be overwritten ("same id, different certificate").
        public static readonly string CertificatePath = Path.Combine(DataFolderPath, "Certificate");
        // Shipped, read-only default certificate(s) that ride along inside the install folder/bundle.
        public static readonly string BundledCertificatePath = Path.Combine(FolderPath, "Assets", "Certificate");

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
        public bool ShowAllJellyfinVersions { get; set; } = false;
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
        public string TvAppChannelsJson { get; set; } = "";  // JSON array of {name,url} for TVApp
        public bool TvAppUseOblongIcon { get; set; } = false;  // swap TVApp's icon for the 16:9 Tizen 5.5 variant
        public bool LitefinUseOblongIcon { get; set; } = false;  // swap Litefin's icon for the 16:9 Tizen 5.5 variant
        public string ManualDuids { get; set; } = "";  // extra Tizen DUIDs to pre-authorize in the distributor cert (one per line / comma-separated)

        // ----- Updater settings -----
        public bool CheckForUpdatesOnStartup { get; set; } = true;
        public string SkippedUpdateVersion { get; set; } = string.Empty;
        public DateTime? LastUpdateCheck { get; set; } = null;

        // ----- Application-scoped settings (readonly at runtime) -----
        // [JsonIgnore] so these always reflect the shipped code defaults. If they were
        // serialized, a preserved settings.json would freeze them at the value first
        // written — e.g. an upgraded user would keep seeing their old AppVersion and
        // stale endpoint URLs after an update.
        [JsonIgnore]
        public string AuthorEndpoint { get; set; } = "https://dev.tizen.samsung.com/apis/v2/authors";
        [JsonIgnore]
        public string AppVersion { get; set; } = "v2.5.4";
        [JsonIgnore]
        public string TizenSdb { get; set; } = "https://api.github.com/repos/PatrickSt1991/tizen-sdb/releases";
        [JsonIgnore]
        public string JellyfinAvReleaseFork { get; set; } = "https://api.github.com/repos/asamahy/tizen-jellyfin-avplay/releases";
        [JsonIgnore]
        public string ReleaseInfo { get; set; } = "https://raw.githubusercontent.com/jeppevinkel/jellyfin-tizen-builds/refs/heads/master/README.md";
        [JsonIgnore]
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
            MigrateCertificatesIfNeeded();

            try
            {
                // One-time migration: if no settings exist in the new per-user location but a
                // legacy file sits next to the binary, adopt it and persist to the new location.
                if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
                {
                    var legacyJson = File.ReadAllText(LegacyFilePath);
                    var legacy = JsonSerializer.Deserialize<AppSettings>(legacyJson);
                    if (legacy != null)
                    {
                        _instance = legacy;
                        legacy.Save();
                        return _instance;
                    }
                }

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

        /// <summary>
        /// One-time move of the user's generated signing cert from the old in-bundle location
        /// (<see cref="BundledCertificatePath"/>/Jelly2Sams) to the per-user data dir, so it isn't
        /// lost on the next app/bundle update. The shipped default cert stays in the bundle.
        /// On bundle-replacing updates the old copy may already be gone — then this is a no-op and
        /// a fresh cert is generated once on the next install.
        /// </summary>
        private static void MigrateCertificatesIfNeeded()
        {
            try
            {
                var legacyGenerated = Path.Combine(BundledCertificatePath, "Jelly2Sams");
                var newGenerated = Path.Combine(CertificatePath, "Jelly2Sams");

                if (Directory.Exists(legacyGenerated) && !Directory.Exists(newGenerated))
                    CopyDirectory(legacyGenerated, newGenerated);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Certificate migration failed: {ex.Message}");
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dest, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }
}
