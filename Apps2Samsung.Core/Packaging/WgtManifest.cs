using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads values from a <c>.wgt</c> package's <c>config.xml</c> that both heads need before install.
    /// Kept in Core so the desktop and mobile installers share one implementation instead of each
    /// parsing the manifest their own way.
    /// </summary>
    public static class WgtManifest
    {
        private static readonly XNamespace Tizen = "http://tizen.org/ns/widgets";

        // <tizen:application ... required_version="X.Y" ...> — the minimum Tizen platform the package runs on.
        private static readonly Regex RequiredVersionRegex = new(
            @"<tizen:application\b[^>]*\brequired_version\s*=\s*""(?<version>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Reads the package's declared minimum Tizen version (<c>required_version</c>) from its
        /// <c>config.xml</c>. Returns null if the file is missing, has no <c>config.xml</c>, or declares
        /// no required version.
        /// </summary>
        public static async Task<string?> ReadRequiredVersionAsync(string wgtPath)
        {
            if (string.IsNullOrEmpty(wgtPath) || !File.Exists(wgtPath))
                return null;

            try
            {
                using var fs = File.OpenRead(wgtPath);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                var configEntry = archive.GetEntry("config.xml");
                if (configEntry is null)
                    return null;

                using var reader = new StreamReader(configEntry.Open(), Encoding.UTF8);
                var configContent = await reader.ReadToEndAsync();

                var match = RequiredVersionRegex.Match(configContent);
                return match.Success ? match.Groups["version"].Value : null;
            }
            catch
            {
                // A corrupt/unreadable package shouldn't crash the version gate — treat as "no requirement"
                // and let the actual install surface the real error.
                return null;
            }
        }

        /// <summary>
        /// Reads the package's Tizen application id and package id from its <c>config.xml</c>
        /// (<c>&lt;tizen:application id="Pkg.App" package="Pkg"&gt;</c>). Returns nulls for anything
        /// unreadable rather than throwing — every caller treats "unknown" as a normal case.
        /// </summary>
        public static async Task<(string? ApplicationId, string? PackageId)> ReadIdsAsync(string wgtPath)
        {
            var config = await ReadConfigAsync(wgtPath);
            return config is null ? (null, null) : ParseIds(config);
        }

        /// <summary>The package id (the <c>package</c> attribute) of any app's package.</summary>
        public static async Task<string?> ReadPackageIdAsync(string wgtPath) =>
            (await ReadIdsAsync(wgtPath)).PackageId;

        /// <summary>The Tizen application id (<c>Pkg.App</c>) of any app's package.</summary>
        public static async Task<string?> ReadApplicationIdAsync(string wgtPath) =>
            (await ReadIdsAsync(wgtPath)).ApplicationId;

        /// <summary>As <see cref="ReadPackageIdAsync"/>, for a package already extracted to a workspace.</summary>
        public static async Task<string?> ReadExtractedPackageIdAsync(string workspaceRoot)
        {
            var configPath = Path.Combine(workspaceRoot, "config.xml");
            if (!File.Exists(configPath))
                return null;

            var config = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            return ParseIds(config).PackageId;
        }

        /// <summary>
        /// The package id of an extracted package, but ONLY when it is a Jellyfin one
        /// (<c>&lt;pkg&gt;.Jellyfin</c>). The Jellyfin-specific patches use this so they can never
        /// touch a non-Jellyfin package that happens to be in the workspace.
        /// </summary>
        public static async Task<string?> ReadExtractedJellyfinPackageIdAsync(string workspaceRoot)
        {
            var configPath = Path.Combine(workspaceRoot, "config.xml");
            if (!File.Exists(configPath))
                return null;

            var config = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            var match = RegexPatterns.WgtConfig.TizenApplicationId.Match(config);
            return match.Success ? match.Groups["pkg"].Value : null;
        }

        /// <summary>Reads <c>config.xml</c> out of a .wgt, or null when there isn't one to read.</summary>
        internal static async Task<string?> ReadConfigAsync(string wgtPath)
        {
            if (string.IsNullOrEmpty(wgtPath) || !File.Exists(wgtPath))
                return null;

            try
            {
                using var fs = File.OpenRead(wgtPath);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                var configEntry = archive.GetEntry("config.xml");
                if (configEntry is null)
                    return null;

                using var reader = new StreamReader(configEntry.Open(), Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            catch
            {
                // A corrupt or half-downloaded package: the install itself will report the real error.
                return null;
            }
        }

        // Parsed as XML first — attribute order and whitespace vary between packagers, and the regex
        // below only matches the id="Pkg.App" package="Pkg" order. The regex stays as the fallback for
        // a config.xml that isn't well-formed enough for XDocument (some third-party packagers).
        private static (string? ApplicationId, string? PackageId) ParseIds(string config)
        {
            try
            {
                var application = XDocument.Parse(config)
                    .Descendants(Tizen + "application")
                    .FirstOrDefault();

                if (application is not null)
                {
                    var applicationId = application.Attribute("id")?.Value;
                    // Some packagers omit the package attribute; the id's own prefix is the package id.
                    var packageId = application.Attribute("package")?.Value
                        ?? applicationId?.Split('.').FirstOrDefault();
                    if (!string.IsNullOrEmpty(applicationId) || !string.IsNullOrEmpty(packageId))
                        return (applicationId, packageId);
                }
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to the regex.
            }

            var match = RegexPatterns.WgtConfig.TizenPackageIdAny.Match(config);
            if (!match.Success)
                return (null, null);

            var pkg = match.Groups["pkg"].Value;
            var idMatch = Regex.Match(config, @"<tizen:application\b[^>]*\bid\s*=\s*""(?<id>[^""]+)""",
                RegexOptions.IgnoreCase);
            return (idMatch.Success ? idMatch.Groups["id"].Value : null, pkg);
        }

        /// <summary>
        /// True when the TV can't run the package because its Tizen version is older than the package's
        /// <paramref name="requiredVersion"/>. Lenient: returns false (allow the install) when the TV
        /// version is unknown or the required version can't be parsed, so a malformed value never wrongly
        /// blocks an install — the install itself will still surface any real incompatibility.
        /// </summary>
        public static bool RequiresNewerTizen(Version? tvVersion, string? requiredVersion) =>
            tvVersion is not null
            && Version.TryParse(requiredVersion, out var required)
            && tvVersion < required;
    }
}
