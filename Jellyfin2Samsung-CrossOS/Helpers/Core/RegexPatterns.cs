using System.Text.RegularExpressions;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Centralized regex patterns used throughout the application.
    /// Pre-compiled patterns improve performance for frequently used expressions.
    /// </summary>
    public static partial class RegexPatterns
    {
        /// <summary>
        /// Patterns for version parsing and extraction.
        /// </summary>
        public static class Version
        {
            /// <summary>
            /// Pattern to extract version from a file name (e.g., "TizenSdb_v1.0.0.exe" -> "v1.0.0").
            /// </summary>
            public const string FileNameVersionPattern = @"_([v]?\d+\.\d+\.\d+)";

            /// <summary>
            /// Pre-compiled regex for file name version extraction.
            /// </summary>
            public static readonly Regex FileNameVersion = new(FileNameVersionPattern, RegexOptions.Compiled);
        }

        /// <summary>
        /// Patterns for Tizen device capability parsing.
        /// </summary>
        public static class TizenCapability
        {
            /// <summary>
            /// Pattern to extract platform version from Tizen capability output.
            /// </summary>
            public const string PlatformVersionPattern = @"platform_version:\s*([\d.]+)";

            /// <summary>
            /// Pattern to extract SDK tool path from Tizen capability output.
            /// </summary>
            public const string SdkToolPathPattern = @"sdk_toolpath:\s*([^\r\n]+)";

            /// <summary>
            /// Pattern to detect app uninstall test failure in Tizen diagnose output.
            /// </summary>
            public const string AppUninstallFailedPattern = @"Testing '0 vd_appuninstall test':\s*FAILED";

            /// <summary>
            /// Pre-compiled regex for platform version extraction.
            /// </summary>
            public static readonly Regex PlatformVersion = new(PlatformVersionPattern, RegexOptions.Compiled);

            /// <summary>
            /// Pre-compiled regex for SDK tool path extraction.
            /// </summary>
            public static readonly Regex SdkToolPath = new(SdkToolPathPattern, RegexOptions.Compiled);

            /// <summary>
            /// Pre-compiled regex for app uninstall test failure detection.
            /// </summary>
            public static readonly Regex AppUninstallFailed = new(
                AppUninstallFailedPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Patterns for Tizen app information parsing.
        /// </summary>
        public static class TizenApp
        {
            /// <summary>
            /// Pattern template to extract app block by title from Tizen apps output.
            /// Use string.Format or interpolation with the app title.
            /// </summary>
            public const string AppBlockByTitleTemplate = @"(^\s*-+app_title\s*=\s*{0}.*?)(?=^\s*-+app_title|\Z)";

            /// <summary>
            /// Pattern to extract Tizen app ID from an app block.
            /// </summary>
            public const string AppTizenIdPattern = @"app_tizen_id\s*=\s*([A-Za-z0-9._]+)";

            /// <summary>
            /// Pattern to extract Tizen app ID with delimiter.
            /// </summary>
            public const string AppTizenIdWithDelimiterPattern = @"app_tizen_id\s*=\s*([A-Za-z0-9._-]+?)(?=-{3,})";

            /// <summary>
            /// Pre-compiled regex for app Tizen ID extraction.
            /// </summary>
            public static readonly Regex AppTizenId = new(
                AppTizenIdPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for app Tizen ID with delimiter extraction.
            /// </summary>
            public static readonly Regex AppTizenIdWithDelimiter = new(
                AppTizenIdWithDelimiterPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Creates a regex to find an app block by title.
            /// </summary>
            /// <param name="appTitle">The app title to search for.</param>
            /// <returns>A regex instance for matching the app block.</returns>
            public static Regex CreateAppBlockByTitleRegex(string appTitle)
            {
                var escapedTitle = Regex.Escape(appTitle);
                var pattern = string.Format(AppBlockByTitleTemplate, escapedTitle);
                return new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);
            }
        }

        /// <summary>
        /// Patterns for WGT package config.xml parsing.
        /// </summary>
        public static class WgtConfig
        {
            /// <summary>
            /// Pattern to extract package ID from Tizen application element in config.xml.
            /// </summary>
            public const string TizenApplicationIdPattern =
                @"<tizen:application\s+id=""(?<pkg>[A-Za-z0-9]+)\.Jellyfin""\s+package=""\k<pkg>""";

            /// <summary>
            /// Pre-compiled regex for Tizen application ID extraction.
            /// </summary>
            public static readonly Regex TizenApplicationId = new(
                TizenApplicationIdPattern,
                RegexOptions.Compiled | RegexOptions.Multiline);

            /// <summary>
            /// Creates a pattern to match and replace a specific package ID in config.xml.
            /// </summary>
            /// <param name="packageId">The package ID to match.</param>
            /// <returns>The pattern string.</returns>
            public static string CreatePackageIdReplacePattern(string packageId)
            {
                return $@"<tizen:application\s+id=""{packageId}\.Jellyfin""\s+package=""{packageId}""";
            }
        }

        /// <summary>
        /// Patterns for HTML parsing and manipulation.
        /// </summary>
        public static class Html
        {
            /// <summary>
            /// Pattern to extract href from link elements.
            /// </summary>
            public const string LinkHrefPattern = @"<link[^>]+href=[""']([^""']+)[""'][^>]*>";

            /// <summary>
            /// Pattern to extract src from script elements.
            /// </summary>
            public const string ScriptSrcPattern = @"<script[^>]+src=[""']([^""']+)[""'][^>]*>[\s\S]*?<\/script>";

            /// <summary>
            /// Pattern to match base tag elements for replacement.
            /// </summary>
            public const string BaseTagPattern = @"<base[^>]+>";

            /// <summary>
            /// Pattern to rewrite local paths (src/href with /web/ prefix).
            /// </summary>
            public const string LocalPathsPattern = @"(src|href)=""[^""]*/web/([^""]+)""";

            /// <summary>
            /// Pattern to match Content-Security-Policy meta tags.
            /// </summary>
            public const string CspMetaPattern = @"<meta[^>]*Content-Security-Policy[^>]*>";

            /// <summary>
            /// Pre-compiled regex for link href extraction.
            /// </summary>
            public static readonly Regex LinkHref = new(
                LinkHrefPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for script src extraction.
            /// </summary>
            public static readonly Regex ScriptSrc = new(
                ScriptSrcPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for base tag matching.
            /// </summary>
            public static readonly Regex BaseTag = new(
                BaseTagPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for local paths rewriting.
            /// </summary>
            public static readonly Regex LocalPaths = new(
                LocalPathsPattern,
                RegexOptions.Compiled);

            /// <summary>
            /// Pre-compiled regex for CSP meta tag matching.
            /// </summary>
            public static readonly Regex CspMeta = new(
                CspMetaPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Patterns for network and MAC address parsing.
        /// </summary>
        public static class Network
        {
            /// <summary>
            /// Pattern to extract MAC address from ARP output.
            /// </summary>
            public const string MacAddressPattern = @"([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})";

            /// <summary>
            /// Pre-compiled regex for MAC address extraction.
            /// </summary>
            public static readonly Regex MacAddress = new(MacAddressPattern, RegexOptions.Compiled);
        }

        /// <summary>
        /// Patterns for command line argument parsing.
        /// </summary>
        public static class CommandLine
        {
            /// <summary>
            /// Pattern to parse command line arguments (handles quoted strings).
            /// </summary>
            public const string ArgumentsPattern = @"[\""].+?[\""]|[^ ]+";

            /// <summary>
            /// Pre-compiled regex for argument parsing.
            /// </summary>
            public static readonly Regex Arguments = new(ArgumentsPattern, RegexOptions.Compiled);
        }

        /// <summary>
        /// Patterns for build info markdown parsing.
        /// </summary>
        public static class BuildInfo
        {
            /// <summary>
            /// Pattern to extract versions table from markdown.
            /// </summary>
            public const string VersionsTablePattern = @"## Versions\s*\n(?<table>(\|[^\n]+\n)+)";

            /// <summary>
            /// Pattern to extract applications table from markdown.
            /// </summary>
            public const string ApplicationsTablePattern =
                @"\|\s*🧩 Application\s*\|\s*📝 Description\s*\|\s*🔗 Repository\s*\|\s*\n(?<table>(\|[^\n]+\n)+)";

            /// <summary>
            /// Pattern to extract table rows with 2 columns.
            /// </summary>
            public const string TableRow2ColumnsPattern = @"^\|([^|]+)\|([^|]+)\|";

            /// <summary>
            /// Pattern to extract table rows with 3 columns.
            /// </summary>
            public const string TableRow3ColumnsPattern = @"^\|([^|]+)\|([^|]+)\|([^|]+)\|";

            /// <summary>
            /// Pattern to remove markdown bold formatting.
            /// </summary>
            public const string MarkdownBoldPattern = @"\*\*(.*?)\*\*";

            /// <summary>
            /// Pattern to remove emoji characters.
            /// </summary>
            public const string EmojiRangePattern = @"[\u2600-\u27BF]";

            /// <summary>
            /// Pre-compiled regex for versions table extraction.
            /// </summary>
            public static readonly Regex VersionsTable = new(
                VersionsTablePattern,
                RegexOptions.Compiled | RegexOptions.Multiline);

            /// <summary>
            /// Pre-compiled regex for applications table extraction.
            /// </summary>
            public static readonly Regex ApplicationsTable = new(
                ApplicationsTablePattern,
                RegexOptions.Compiled | RegexOptions.Multiline);

            /// <summary>
            /// Pre-compiled regex for 2-column table rows.
            /// </summary>
            public static readonly Regex TableRow2Columns = new(
                TableRow2ColumnsPattern,
                RegexOptions.Compiled | RegexOptions.Multiline);

            /// <summary>
            /// Pre-compiled regex for 3-column table rows.
            /// </summary>
            public static readonly Regex TableRow3Columns = new(
                TableRow3ColumnsPattern,
                RegexOptions.Compiled | RegexOptions.Multiline);

            /// <summary>
            /// Pre-compiled regex for markdown bold removal.
            /// </summary>
            public static readonly Regex MarkdownBold = new(MarkdownBoldPattern, RegexOptions.Compiled);

            /// <summary>
            /// Pre-compiled regex for emoji removal.
            /// </summary>
            public static readonly Regex EmojiRange = new(EmojiRangePattern, RegexOptions.Compiled);
        }

        /// <summary>
        /// Patterns for plugin configuration parsing.
        /// </summary>
        public static class PluginConfig
        {
            /// <summary>
            /// Pattern to extract defaultSkin value from JSON config.
            /// </summary>
            public const string DefaultSkinPattern = @"""defaultSkin""\s*:\s*""([^""]+)""";

            /// <summary>
            /// Pre-compiled regex for defaultSkin extraction.
            /// </summary>
            public static readonly Regex DefaultSkin = new(
                DefaultSkinPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Patterns for extension parsing in Tizen package manager output.
        /// </summary>
        public static class Extension
        {
            /// <summary>
            /// Pattern to parse extension entries from package manager output.
            /// </summary>
            public const string ExtensionEntryPattern =
                @"Index\s*:\s*(\d+)\s+Name\s*:\s*(.*?)\s+Repository\s*:\s*.*?\s+Id\s*:\s*.*?\s+Vendor\s*:\s*.*?\s+Description\s*:\s*.*?\s+Default\s*:\s*.*?\s+Activate\s*:\s*(true|false)";

            /// <summary>
            /// Pre-compiled regex for extension entry parsing.
            /// </summary>
            public static readonly Regex ExtensionEntry = new(
                ExtensionEntryPattern,
                RegexOptions.Compiled | RegexOptions.Singleline);
        }

        /// <summary>
        /// Patterns for KefinTweaks plugin patching.
        /// </summary>
        public static class KefinTweaks
        {
            /// <summary>
            /// Pattern to detect KefinTweaks loader script reference in public.js.
            /// </summary>
            public const string LoaderPattern =
                @"script\.src\s*=\s*['""]https:\/\/cdn\.jsdelivr\.net\/gh\/ranaldsgift\/KefinTweaks[^'""]+['""]";

            /// <summary>
            /// Pattern to match and replace kefinTweaksRoot configuration.
            /// </summary>
            public const string TweaksRootPattern =
                @"""kefinTweaksRoot""\s*:\s*""https:\/\/cdn\.jsdelivr\.net\/gh\/ranaldsgift\/KefinTweaks@latest\/""";

            /// <summary>
            /// Pattern to extract script entries from injector.js.
            /// </summary>
            public const string ScriptEntryPattern = @"script\s*:\s*""([^""]+)""";

            /// <summary>
            /// Pre-compiled regex for loader detection.
            /// </summary>
            public static readonly Regex Loader = new(
                LoaderPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for tweaks root replacement.
            /// </summary>
            public static readonly Regex TweaksRoot = new(
                TweaksRootPattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

            /// <summary>
            /// Pre-compiled regex for script entry extraction.
            /// </summary>
            public static readonly Regex ScriptEntry = new(
                ScriptEntryPattern,
                RegexOptions.Compiled);
        }

        /// <summary>
        /// Patterns for plugin name cleaning and normalization.
        /// </summary>
        public static class PluginName
        {
            /// <summary>
            /// Pattern to remove non-alphanumeric characters from plugin names.
            /// </summary>
            public const string NonAlphanumericPattern = @"[^a-z0-9]";

            /// <summary>
            /// Pre-compiled regex for plugin name cleaning.
            /// </summary>
            public static readonly Regex NonAlphanumeric = new(
                NonAlphanumericPattern,
                RegexOptions.Compiled);
        }
    }
}
