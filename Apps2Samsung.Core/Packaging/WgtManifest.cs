using System;
using System.Collections.Generic;
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

        // <tizen:application ... required_version="X.Y" ...>: the minimum Tizen platform the package runs on.
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
                // A corrupt/unreadable package shouldn't crash the version gate: treat as "no requirement"
                // and let the actual install surface the real error.
                return null;
            }
        }

        /// <summary>
        /// Reads the package's Tizen application id and package id from its <c>config.xml</c>
        /// (<c>&lt;tizen:application id="Pkg.App" package="Pkg"&gt;</c>). Returns nulls for anything
        /// unreadable rather than throwing: every caller treats "unknown" as a normal case.
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
                var required = RequiredVersionRegex.Match(config);
                return new WgtPackageInfo(ParseWidgetVersion(config), applicationId, packageId,
                    required.Success ? required.Groups["version"].Value.Trim() : null);
            }

            var native = await ReadEntryAsync(packagePath, "tizen-manifest.xml");
            return native is null ? new WgtPackageInfo(null, null, null, null) : ParseNativeManifest(native);
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
                    return new WgtPackageInfo(null, null, null, null);
                var packageId = root.Attribute("package")?.Value;
                var version = root.Attribute("version")?.Value;
                var apiVersion = root.Attribute("api-version")?.Value;
                var appId = root.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName is "ui-application" or "service-application" or "widget-application" or "watch-application")
                    ?.Attribute("appid")?.Value;
                return new WgtPackageInfo(Clean(version), Clean(appId), Clean(packageId), Clean(apiVersion));
            }
            catch (System.Xml.XmlException)
            {
                return new WgtPackageInfo(null, null, null, null);
            }

            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static readonly XNamespace Widgets = "http://www.w3.org/ns/widgets";

        /// <summary>
        /// The icon a package ships, for the catalog (#1000, TV-Store#66). A web package names it in
        /// <c>config.xml</c> (<c>&lt;icon src="..."&gt;</c>, the first when there are several, a path
        /// relative to the package root, or to <c>res/wgt/</c> in a hybrid package); a native one in
        /// <c>tizen-manifest.xml</c> (<c>&lt;icon&gt;</c>
        /// under the application element), with the file under <c>shared/res/</c>, next to the
        /// manifest, or under <c>res/</c>. Only PNG, JPEG, GIF and WebP count, decided by the bytes
        /// and not the name: an SVG can carry scripts, and TV-Store shows nothing else. Null when the
        /// package has no readable icon of those kinds; never throws.
        /// </summary>
        public static async Task<PackageIcon?> ReadIconAsync(string packagePath)
        {
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                return null;

            try
            {
                using var fs = File.OpenRead(packagePath);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Read);

                var candidates = new List<string>();
                var config = await ReadEntryAsync(archive, "config.xml");
                if (config is not null)
                {
                    var src = ParseWidgetIcon(config);
                    if (src is not null)
                    {
                        candidates.Add(src);
                        // A hybrid package (a web app with a native service) keeps the web app under
                        // res/wgt/, and config.xml's paths are relative to that: seven community
                        // packages ship icon.png there and nowhere else (#1000).
                        candidates.Add("res/wgt/" + src.Replace('\\', '/').TrimStart('.', '/'));
                    }
                }
                else
                {
                    var manifest = await ReadEntryAsync(archive, "tizen-manifest.xml");
                    var name = manifest is null ? null : ParseNativeIcon(manifest);
                    if (name is not null)
                    {
                        candidates.Add("shared/res/" + name);
                        candidates.Add(name);
                        candidates.Add("res/" + name);
                        // A manifest that names a path (res/icons/app.png, /opt/usr/.../app.png) still
                        // ships the file by its bare name in one of the three places.
                        var file = name.Split('/', '\\').Last();
                        if (file != name && file.Length > 0)
                        {
                            candidates.Add("shared/res/" + file);
                            candidates.Add(file);
                            candidates.Add("res/" + file);
                        }
                    }
                }

                foreach (var candidate in candidates)
                {
                    var entry = FindEntry(archive, candidate);
                    if (entry is null)
                        continue;

                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    var bytes = buffer.ToArray();
                    var extension = ImageExtension(bytes);
                    return extension is null ? null : new PackageIcon(bytes, extension);
                }

                return null;
            }
            catch
            {
                // A corrupt or half-downloaded package: no icon is the normal answer, not a crash.
                return null;
            }
        }

        private static string? ParseWidgetIcon(string config)
        {
            try
            {
                var root = XDocument.Parse(config).Root;
                var icon = root?.Descendants(Widgets + "icon").FirstOrDefault(e => e.Attribute("src") is not null)
                    ?? root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "icon" && e.Attribute("src") is not null);
                var src = icon?.Attribute("src")?.Value;
                if (!string.IsNullOrWhiteSpace(src))
                    return src.Trim();
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to the regex.
            }
            var match = Regex.Match(config, @"<(?:\w+:)?icon\b[^>]*\bsrc\s*=\s*""(?<src>[^""]+)""", RegexOptions.IgnoreCase);
            return match.Success && !string.IsNullOrWhiteSpace(match.Groups["src"].Value) ? match.Groups["src"].Value.Trim() : null;
        }

        private static string? ParseNativeIcon(string manifest)
        {
            try
            {
                var root = XDocument.Parse(manifest).Root;
                if (root is not null)
                {
                    var icons = root.Descendants().Where(e => e.Name.LocalName == "icon").ToList();
                    // The application's own icon first; a package-level <icon> only when there is no other.
                    var icon = icons.FirstOrDefault(e => e.Parent?.Name.LocalName is "ui-application" or "service-application" or "widget-application" or "watch-application")
                        ?? icons.FirstOrDefault();
                    var name = icon?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                        return name.Trim();
                }
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to the regex.
            }
            var match = Regex.Match(manifest, @"<icon\b[^>]*>(?<file>[^<]+)</icon>", RegexOptions.IgnoreCase);
            return match.Success && !string.IsNullOrWhiteSpace(match.Groups["file"].Value) ? match.Groups["file"].Value.Trim() : null;
        }

        // The entry at a package-relative path, however the packager spelled it: "./icon.png",
        // "/icon.png" and "Icon.PNG" all find icon.png.
        private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
        {
            var wanted = Normalize(path);
            if (wanted.Length == 0)
                return null;
            return archive.GetEntry(wanted)
                ?? archive.Entries.FirstOrDefault(e => string.Equals(Normalize(e.FullName), wanted, StringComparison.OrdinalIgnoreCase));

            static string Normalize(string p)
            {
                p = p.Replace('\\', '/');
                while (p.StartsWith("./", StringComparison.Ordinal))
                    p = p.Substring(2);
                return p.TrimStart('/');
            }
        }

        /// <summary>
        /// The file extension for an image from its first bytes (png, jpg, gif, webp), null for
        /// anything else. The same four signatures TV-Store accepts (tizen-app/service/icons.js).
        /// </summary>
        internal static string? ImageExtension(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "jpg";
            if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'8')
                return "gif";
            if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
                return "webp";
            return null;
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
                return await ReadEntryAsync(archive, entryName);
            }
            catch
            {
                // A corrupt or half-downloaded package: the install itself will report the real error.
                return null;
            }
        }

        private static async Task<string?> ReadEntryAsync(ZipArchive archive, string entryName)
        {
            var entry = archive.GetEntry(entryName);
            if (entry is null)
                return null;

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        // Parsed as XML first: attribute order and whitespace vary between packagers, and the regex
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
        /// blocks an install: the install itself will still surface any real incompatibility.
        /// </summary>
        public static bool RequiresNewerTizen(Version? tvVersion, string? requiredVersion) =>
            tvVersion is not null
            && Version.TryParse(requiredVersion, out var required)
            && tvVersion < required;
    }

    /// <summary>
    /// What a package's manifest says about itself; see <see cref="WgtManifest.ReadPackageInfoAsync"/>.
    /// <paramref name="RequiredVersion"/> is the Tizen version it asks for: <c>required_version</c> on
    /// a web package, <c>api-version</c> on a native one.
    /// </summary>
    public sealed record WgtPackageInfo(string? Version, string? ApplicationId, string? PackageId, string? RequiredVersion);

    /// <summary>
    /// A package's icon as it ships (<see cref="WgtManifest.ReadIconAsync"/>): the file's bytes and
    /// the extension its signature says it is (png, jpg, gif or webp).
    /// </summary>
    public sealed record PackageIcon(byte[] Bytes, string Extension);
}
