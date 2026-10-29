namespace Apps2Samsung.Helpers.Core
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
            public const string CustomWgtFile = "Custom WGT / TPK";
        }

        /// <summary>
        /// Tizen installation error codes returned by the SDB tool.
        /// </summary>
        public static class TizenErrorCodes
        {
            public const string DownloadFailed116 = "download failed[116]";
            public const string InstallFailed118012 = "install failed[118012]";
            public const string InstallFailed118Minus12 = "install failed[118, -12]";
            // [118, -12] comes with a reason from Tizen's signature validator, "Check certificate error
            // : :<text>:<code>". Only one of those texts is the "installed with another certificate"
            // case that removing the old copy fixes; the others are the TV rejecting the certificate
            // itself (#907), where a removal prompt is wrong and only the TV's clock or a new
            // certificate can help.
            public const string CheckCertificateError = "Check certificate error";
            public const string CertificateSameIdDifferent = "same id and different certificate";
            public const string CertificateInvalidFormat = "Invalid format of certificate";   // <-2>
            public const string CertificateInvalidChain = "Invalid certificate chain";        // <-3>
            public const string CertificateExpired = "expired";                               // <-6>
            public const string CertificateNotYetValid = "not valid yet";                     // <-7>
            // API-version incompatibility: the package targets a higher Tizen API level than the TV
            // supports. Distinct from the generic [118] and the [118, -12] cert mismatch.
            public const string InstallFailed118Minus4 = "install failed[118, -4]";
            // The TV's security manager refused the package id itself: SECURITY_MANAGER_ERROR_INPUT_PARAM
            // ("Security error : :Invalid function parameter was given:<2>"). Note the comma — it does NOT
            // contain the plain "install failed[118]" token, so it used to fall through to the generic
            // branch and dump the raw wascmd output at the user (#702, #422).
            public const string InstallFailed118Minus22 = "install failed[118, -22]";
            // Tizen 4 (2018 sets, sdbd 2.2.x) prints the same refusal as a six-digit code, the way
            // [118012] is the old spelling of [118, -12]. Also not a substring of "install failed[118]".
            public const string InstallFailed118022 = "install failed[118022]";
            public const string InstallFailed118 = "install failed[118]";
            public const string Installing100 = "installing[100]";
            public const string InstallCompleted = "install completed";
            public const string ResignFailed = "Re-sign failed";
            public const string Failed = "failed";
            public const string NotInstalled = "failed[132]";
            public const string TransportConnectionLost = "transport connection";
            public const string ConnectionResetByPeer = "connection reset by peer";
        }

        /// <summary>
        /// Default values used throughout the application.
        /// </summary>
        public static class Defaults
        {
            public const string TizenOsVersion = "7.0";
            public const string SdkToolPath = "/opt/usr/apps/tmp";
            public const string HomeDeveloperPath = "/home/developer";
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
            // Plugin scripts are transpiled to ES2015, which Tizen 4.0 (Chromium 56) runs. Tizen 3.0
            // (Chromium 47) lacks parts of ES2015 and esbuild can't go lower, so plugins are skipped there (#801).
            public const string PluginScriptsMin = "4.0";
            // Background <tizen:service> components are only reliably installable on
            // Samsung TVs from Tizen 4.0 onward. Below this, such a component must be
            // stripped or the whole package is rejected with a generic install failed[118].
        }

        /// <summary>
        /// Network ports used by the application.
        /// </summary>
        public static class Ports
        {
            public const int TizenDevPort = 26101;
            public const int SamsungTvApiPort = 8001;
            // Samsung login callback port lives in Apps2Samsung.Samsung.SamsungOAuth.CallbackPort.
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
            public const string EsbuildWindows = "win-x64";
            public const string EsbuildLinux = "linux-x64";
            public const string EsbuildMacOsX64 = "macos-x64";
            public const string EsbuildMacOsArm64 = "macos-arm64";
            public const string EsbuildExecutable = "esbuild";
            public const string EsbuildExecutableWindows = "esbuild.exe";
        }

        /// <summary>
        /// HTTP and API related constants.
        /// </summary>
        public static class Api
        {
            public const string UserAgent = "SamsungJellyfinInstaller/1.0";

            // Jellyfin's "MediaBrowser" authorization scheme. Since Jellyfin 12 the client identity
            // (Client/Device/DeviceId/Version) must travel in the standard Authorization header:
            // the old X-Emby-Authorization header is only honoured when the server admin enables
            // "legacy authorization", and /Users/AuthenticateByName rejects a login without an
            // identity (#655). Jellyfin 10.8+ accepts the same header, so one format serves both.
            // {0} client, {1} device name, {2} device id, {3} app version, {4} access token.
            public const string MediaBrowserClientName = "Apps2Samsung";
            public const string MediaBrowserAuthHeader = "MediaBrowser Client=\"{0}\", Device=\"{1}\", DeviceId=\"{2}\", Version=\"{3}\"";
            public const string MediaBrowserAuthHeaderWithToken = MediaBrowserAuthHeader + ", Token=\"{4}\"";
            public const string JsonContentType = "application/json";
        }

        /// <summary>
        /// Samsung API endpoints and OAuth constants.
        /// </summary>
        public static class Samsung
        {
            public const string LoopbackHost = "localhost";
            // OAuth endpoint + parameters (SignInGate URL, client id, state, token type, callback
            // path) are the single source of truth in Apps2Samsung.Samsung.SamsungOAuth.
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
            // Max device IDs a single Samsung distributor certificate can cover. (Conservative
            // cap; verify against Samsung if you hit it — extra DUIDs beyond this are dropped.)
            public const int MaxDistributorDuids = 10;
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
            public const string RepoOwner = "Apps2Samsung";
            public const string RepoName = "Apps2Samsung";
            public const string AtomFeedUrl = "https://github.com/Apps2Samsung/Apps2Samsung/releases.atom";
            public const string ReleasesPageUrl = "https://github.com/Apps2Samsung/Apps2Samsung/releases";
            public const string LatestReleaseApiUrl = "https://api.github.com/repos/Apps2Samsung/Apps2Samsung/releases/latest";
            public const int UpdateCheckTimeoutSeconds = 10;
        }

        /// <summary>
        /// Where the licence footer and the Contribute rows in Settings lead (#820), the same on both heads.
        /// </summary>
        public static class Links
        {
            public const string Website = "https://apps2samsung.com";
            public const string SourceCode = "https://github.com/Apps2Samsung/Apps2Samsung";
            public const string License = "https://github.com/Apps2Samsung/Apps2Samsung/blob/beta/LICENSE";
            /// <summary>The name in the licence footer; it links to <see cref="Website"/>.</summary>
            public const string AuthorName = "Patrick Stel";
            public const string Translate = "https://crowdin.com/project/jellyfin2samsung";
            /// <summary>The "Contribute an App" form of the community packages repo, opened directly.</summary>
            public const string ContributeApp = "https://github.com/Apps2Samsung/tizen-community-packages/issues/new?template=contribute-app.yml";
            /// <summary>How the package files work, for what the Add an app form doesn't ask (several files, build steps, user settings).</summary>
            public const string PackagesReadme = "https://github.com/Apps2Samsung/tizen-community-packages/blob/main/packages/README.md";
            public const string Discord = "https://discord.gg/7mga3zh8Cv";
            public const string Donate = "https://ko-fi.com/patrickst";
        }

        /// <summary>
        /// Localization keys used for status messages.
        /// </summary>
        public static class LocalizationKeys
        {
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
            public const string PartnerSigningAutoEnabled = "partnerSigningAutoEnabled";
            public const string PackageAndSign = "packageAndSign";
            public const string InstallingPackage = "InstallingPackage";
            public const string InstallingPackageProgress = "InstallingPackageProgress";
            public const string InstallationFailed = "InstallationFailed";
            public const string ConnectionInterrupted = "connectionInterrupted";
            public const string VpnDetected = "vpnDetectedWarning";
            public const string InstallationSuccessful = "InstallationSuccessful";
            public const string InsufficientSpace = "insufficientSpace";
            public const string AuthorMismatch = "AuthorMismatch";
            public const string CertificateMismatch = "certificateMismatch";
            public const string CertificateRejectedInvalidFormat = "certificateRejectedInvalidFormat";
            public const string CertificateRejectedNotYetValid = "certificateRejectedNotYetValid";
            public const string CertificateRejectedExpired = "certificateRejectedExpired";
            public const string CertificateRejectedOther = "certificateRejectedOther";
            public const string CertificateNotYetValid = "certificateNotYetValid";
            public const string CertificateNotYetValidTitle = "certificateNotYetValidTitle";
            public const string CertificateWaiting = "certificateWaiting";
            public const string CertificateWaitCancelled = "certificateWaitCancelled";
            public const string ApiVersionMismatch = "apiVersionMismatch";
            public const string PackageIdBlocked = "packageIdBlocked";
            public const string ConfirmRemoveTitle = "confirmRemoveTitle";
            public const string ConfirmRemove = "confirmRemove";
            public const string RemoveAndInstall = "keyRemoveAndInstall";
            public const string RemoveDeclined = "removeDeclined";
            public const string Cancel = "lblCancel";
            public const string ModifyConfigRequired = "modiyConfigRequired";
            public const string DuidLimitReached = "duidLimitReached";
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
