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

        /// <summary>
        /// The three things a package's manifest says about itself that the catalog records (#820):
        /// the version the TV will report once it is installed, its application id and its package
        /// id. A web package is read from <c>config.xml</c>, a native one from
        /// <c>tizen-manifest.xml</c>, decided by what the file holds rather than its extension (a
        /// native package has shipped under a .wgt name). All null for an unreadable file.
        /// </summary>
        public static async Task<WgtPackageInfo> ReadPackageInfoAsync(string packagePath)
        {
            var config = await ReadConfigAsync(packagePath);
            if (config is not null)
            {
                var (applicationId, packageId) = ParseIds(config);
                return new WgtPackageInfo(ParseWidgetVersion(config), applicationId, packageId);
            }

            var native = await ReadEntryAsync(packagePath, "tizen-manifest.xml");
            return native is null ? new WgtPackageInfo(null, null, null) : ParseNativeManifest(native);
        }

        private static string? ParseWidgetVersion(string config)
        {
            try
            {
                var version = XDocument.Parse(config).Root?.Attribute("version")?.Value;
                if (!string.IsNullOrWhiteSpace(version))
                    return version.Trim();
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to the regex.
            }
            var match = Regex.Match(config, @"<widget\b[^>]*\bversion\s*=\s*""(?<version>[^""]+)""", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["version"].Value.Trim() : null;
        }

        private static WgtPackageInfo ParseNativeManifest(string manifest)
        {
            try
            {
                var root = XDocument.Parse(manifest).Root;
                if (root is null)
                    return new WgtPackageInfo(null, null, null);
                var packageId = root.Attribute("package")?.Value;
                var version = root.Attribute("version")?.Value;
                var appId = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName is "ui-application" or "service-application" or "widget-application" or "watch-application")
                    ?.Attribute("appid")?.Value;
                return new WgtPackageInfo(Clean(version), Clean(appId), Clean(packageId));
            }
            catch (System.Xml.XmlException)
            {
                return new WgtPackageInfo(null, null, null);
            }

            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        /// <summary>Reads <c>config.xml</c> out of a .wgt, or null when there isn't one to read.</summary>
        internal static Task<string?> ReadConfigAsync(string wgtPath) => ReadEntryAsync(wgtPath, "config.xml");

        /// <summary>Reads one root entry out of a package zip, or null when there isn't one to read.</summary>
        private static async Task<string?> ReadEntryAsync(string packagePath, string entryName)
        {
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                return null;

            try
            {
                using var fs = File.OpenRead(packagePath);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read);
                var entry = archive.GetEntry(entryName);
                if (entry is null)
                    return null;

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
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

    /// <summary>What a package's manifest says about itself; see <see cref="WgtManifest.ReadPackageInfoAsync"/>.</summary>
    public sealed record WgtPackageInfo(string? Version, string? ApplicationId, string? PackageId);
}
