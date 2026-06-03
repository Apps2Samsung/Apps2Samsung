namespace Jellyfin2Samsung.Helpers.Core
{
    /// <summary>
    /// Centralized constants for the application.
    /// Eliminates magic strings and numbers scattered throughout the codebase.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Application identifiers and names.
        /// </summary>
        public static class AppIdentifiers
        {
            public const string JellyfinAppName = "Jellyfin";
            public const string Jelly2SamsDefault = "Jelly2Sams (default)";
            public const string Jelly2Sams = "Jelly2Sams";
            public const string CustomWgtFile = "Custom WGT File";
        }

        /// <summary>
        /// Tizen installation error codes returned by the SDB tool.
        /// </summary>
        public static class TizenErrorCodes
        {
            public const string DownloadFailed116 = "download failed[116]";
            public const string InstallFailed118012 = "install failed[118012]";
            public const string InstallFailed118Minus12 = "install failed[118, -12]";
            public const string InstallFailed118 = "install failed[118]";
            public const string Installing100 = "installing[100]";
            public const string InstallCompleted = "install completed";
            public const string ResignFailed = "Re-sign failed";
            public const string Failed = "failed";
            public const string NotInstalled = "failed[132]";
        }

        /// <summary>
        /// Default values used throughout the application.
        /// </summary>
        public static class Defaults
        {
            public const string TizenOsVersion = "7.0";
            public const string SdkToolPath = "/opt/usr/apps/tmp";
            public const string HomeDeveloperPath = "/home/developer";
            public const string TizenSdbDefaultVersion = "v1.0.0";
            public const int SamsungLoginTimeoutMinutes = 5;
            public const int NetworkScanTimeoutMs = 1000;
            public const int HttpRequestTimeoutSeconds = 15;
            public const int WebSocketMonitorDelaySeconds = 10;
        }

        /// <summary>
        /// Tizen version thresholds for feature compatibility.
        /// </summary>
        public static class TizenVersions
        {
            public const string CertificateRequired = "7.0";
            public const string PushInstallMax = "4.0";
            public const string IntermediateVersion = "3.0";
        }

        /// <summary>
        /// Network ports used by the application.
        /// </summary>
        public static class Ports
        {
            public const int TizenDevPort = 26101;
            public const int SamsungTvApiPort = 8001;
            public const int SamsungLoginCallbackPort = 4794;
        }

        /// <summary>
        /// File extensions and patterns.
        /// </summary>
        public static class FilePatterns
        {
            public const string WgtExtension = ".wgt";
            public const string TpkExtension = ".tpk";
            public const string P12Extension = ".p12";
            public const string CsrExtension = ".csr";
            public const string CerExtension = ".cer";
            public const string CrtExtension = ".crt";
            public const string JsExtension = ".js";
            public const string CssExtension = ".css";
            public const string WgtPattern = "*.wgt";
            public const string TpkPattern = "*.tpk";
        }

        /// <summary>
        /// Platform-specific binary names.
        /// </summary>
        public static class PlatformBinaries
        {
            public const string TizenSdbWindowsPattern = "TizenSdb*.exe";
            public const string TizenSdbLinuxX64Pattern = "TizenSdb*_linux-x64";
            public const string TizenSdbLinuxArm64Pattern = "TizenSdb*_linux-arm64";
            public const string TizenSdbMacOsPattern = "TizenSdb*_macos-x64";
            public const string TizenSdbMacOsArm64Pattern = "TizenSdb*_macos-arm64";
            public const string WindowsExtension = ".exe";
            public const string LinuxX64Suffix = "_linux-x64";
            public const string LinuxArm64Suffix = "_linux-arm64";
            public const string MacX64Suffix = "_macos-x64";
            public const string MacArm64Suffix = "_macos-arm64";
            public const string EsbuildWindows = "win-x64";
            public const string EsbuildLinux = "linux-x64";
            public const string EsbuildMacOs = "osx-universal";
            public const string EsbuildExecutable = "esbuild";
            public const string EsbuildExecutableWindows = "esbuild.exe";
        }

        /// <summary>
        /// HTTP and API related constants.
        /// </summary>
        public static class Api
        {
            public const string UserAgent = "SamsungJellyfinInstaller/1.0";
            public const string MediaBrowserAuthHeader = "MediaBrowser Token=\"{0}\"";
            public const string EmbyAuthHeader = "MediaBrowser Client=\"Samsung Jellyfin Installer\", Device=\"PC\", DeviceId=\"samsungjellyfin\", Version=\"1.0.0\"";
            public const string JsonContentType = "application/json";
        }

        /// <summary>
        /// Samsung API endpoints and OAuth constants.
        /// </summary>
        public static class Samsung
        {
            public const string LoopbackHost = "localhost";
            public const string CallbackPath = "/signin/callback";
            public const string OAuthClientId = "v285zxnl3h";
            public const string OAuthState = "accountcheckdogeneratedstatetext";
            public const string TokenType = "TOKEN";
            public const string SignInGateUrl = "https://account.samsung.com/accounts/be1dce529476c1a6d407c4c7578c31bd/signInGate";
            public const string PlatformVd = "VD";
            public const string PrivilegeLevelPublic = "Public";
            public const string DeveloperTypeIndividual = "Individual";
        }

        /// <summary>
        /// Certificate related constants.
        /// </summary>
        public static class Certificate
        {
            public const string AuthorFileName = "author.p12";
            public const string DistributorFileName = "distributor.p12";
            public const string PasswordFileName = "password.txt";
            public const string DeviceProfileFileName = "device-profile.xml";
            public const string AuthorCsrFileName = "author.csr";
            public const string DistributorCsrFileName = "distributor.csr";
            public const string SignedAuthorCerFileName = "signed_author.cer";
            public const string SignedDistributorCerFileName = "signed_distributor.cer";
            public const string AuthorCaFileName = "vd_tizen_dev_author_ca.cer";
            public const string DistributorCaFileName = "vd_tizen_dev_public2.crt";
            public const string KeyAlias = "usercertificate";
            public const string CsrSubjectAuthor = "C=, ST=, L=, O=, OU=, CN=Jelly2Sams";
            public const string CsrSubjectDistributorTemplate = "CN=TizenSDK, OU=, O=, L=, ST=, C=, emailAddress={0}";
            public const string SigningAlgorithm = "SHA256withRSA";
            public const int RsaKeySize = 2048;
        }

        /// <summary>
        /// Jellyfin web app paths and file names.
        /// </summary>
        public static class JellyfinWeb
        {
            public const string IndexHtml = "index.html";
            public const string ConfigJson = "config.json";
            public const string WwwFolder = "www";
            public const string PluginCacheFolder = "plugin_cache";
            public const string CredentialsStorageKey = "jellyfin_credentials";
        }

        /// <summary>
        /// Random string generation character sets.
        /// </summary>
        public static class CharacterSets
        {
            public const string AlphaLower = "abcdefghijklmnopqrstuvwxyz";
            public const string AlphaUpper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            public const string Alpha = AlphaLower + AlphaUpper;
            public const string AlphaNumeric = Alpha + "0123456789";
        }

        /// <summary>
        /// Updater related constants.
        /// </summary>
        public static class Updater
        {
            public const string RepoOwner = "Jellyfin2Samsung";
            public const string RepoName = "Samsung-Jellyfin-Installer";
            public const string AtomFeedUrl = "https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/releases.atom";
            public const string ReleasesPageUrl = "https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer/releases";
            public const string LatestReleaseApiUrl = "https://api.github.com/repos/Jellyfin2Samsung/Samsung-Jellyfin-Installer/releases/latest";
            public const int UpdateCheckTimeoutSeconds = 10;
        }

        /// <summary>
        /// Localization keys used for status messages.
        /// </summary>
        public static class LocalizationKeys
        {
            public const string InstallTizenSdb = "InstallTizenSdb";
            public const string DiagnoseTv = "diagnoseTv";
            public const string AlreadyInstalled = "alreadyInstalled";
            public const string DeleteExistingVersion = "deleteExistingVersion";
            public const string DeleteExistingFailed = "deleteExistingFailed";
            public const string DeleteExistingSuccess = "deleteExistingSuccess";
            public const string DeleteExistingNotAllowed = "deleteExistingNotAllowed";
            public const string ConnectingToDevice = "ConnectingToDevice";
            public const string TvNameNotFound = "TvNameNotFound";
            public const string TvDuidNotFound = "TvDuidNotFound";
            public const string SamsungLogin = "SamsungLogin";
            public const string CreatingCertificateProfile = "CreatingCertificateProfile";
            public const string PackageAndSign = "packageAndSign";
            public const string InstallingPackage = "InstallingPackage";
            public const string InstallationFailed = "InstallationFailed";
            public const string InstallationSuccessful = "InstallationSuccessful";
            public const string InsufficientSpace = "insufficientSpace";
            public const string AuthorMismatch = "AuthorMismatch";
            public const string ModifyConfigRequired = "modiyConfigRequired";
            public const string FailedTizenSdb = "FailedTizenSdb";
            public const string CheckingTizenSdb = "CheckingTizenSdb";
            public const string ScanningNetwork = "ScanningNetwork";
            public const string InitializationFailed = "InitializationFailed";
            public const string NoDevicesFound = "NoDevicesFound";
            public const string NoDevicesFoundRetry = "NoDevicesFoundRetry";
            public const string Ready = "Ready";
            public const string FailedLoadingReleases = "FailedLoadingReleases";
            public const string InvalidDeviceIp = "InvalidDeviceIp";
            public const string LblOther = "lblOther";
            public const string IpNotListed = "IpNotListed";
            public const string IncompatiblePackage = "IncompatiblePackage";
            public const string IncompatiblePackageDetailed = "IncompatiblePackageDetailed";

            // Updater localization keys
            public const string UpdateAvailable = "UpdateAvailable";
            public const string UpdateCurrentVersion = "UpdateCurrentVersion";
            public const string UpdateLatestVersion = "UpdateLatestVersion";
            public const string UpdateReleaseNotes = "UpdateReleaseNotes";
            public const string UpdateManual = "UpdateManual";
            public const string UpdateAutomatic = "UpdateAutomatic";
            public const string UpdateSkip = "UpdateSkip";
            public const string UpdateDownloading = "UpdateDownloading";
            public const string UpdateApplying = "UpdateApplying";
            public const string UpdateApplyingMessage = "UpdateApplyingMessage";
            public const string UpdateError = "UpdateError";
            public const string UpdateCheckFailed = "UpdateCheckFailed";
            public const string UpdateInstallerManagedWindows = "UpdateInstallerManagedWindows";
            public const string UpdateInstallerManagedLinux = "UpdateInstallerManagedLinux";
            public const string UpdateInstallerManagedMac = "UpdateInstallerManagedMac";
        }

        /// <summary>
        /// Esbuild transpilation settings.
        /// </summary>
        public static class Esbuild
        {
            public const string TempFolderName = "J2S_Esbuild";
            public const string TargetEs2015 = "es2015";
        }
    }
}
