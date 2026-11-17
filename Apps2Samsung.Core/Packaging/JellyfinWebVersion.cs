using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// The jellyfin-web version a Jellyfin package was built from (#1113). Every Jellyfin build
    /// declares version 0.1.0 in its config.xml, the wrapper's own and never raised, so the TV's app
    /// list and the catalog's <c>package_version</c> say the same for a build of 10.11 and one of
    /// 12.4. The web client's version is the one that tells them apart, and webpack writes it into
    /// the main bundle as the fallback next to the shell's own:
    /// <c>NativeShell.AppHost.appVersion():"12.4.0"</c>.
    /// </summary>
    public static class JellyfinWebVersion
    {
        private const string WebRoot = "www/";
        // The bundle is minified, so nothing but the two operands of that one expression is relied on.
        private static readonly Regex InBundle = new(
            @"NativeShell\.AppHost\.appVersion\(\)\s*:\s*""(\d+(?:\.\d+){1,3})""",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// The version in <paramref name="packagePath"/>'s main bundle, or null: not a Jellyfin
        /// package, a build from before the bundle read this way, or a file that is not a zip.
        /// </summary>
        public static string? Read(string packagePath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(packagePath);
                // main.jellyfin.bundle.js today; older builds name it main.bundle.js or add a hash.
                var bundles = zip.Entries
                    .Where(e => e.FullName.StartsWith(WebRoot, StringComparison.OrdinalIgnoreCase) &&
                                e.FullName.IndexOf('/', WebRoot.Length) < 0 &&
                                e.Name.StartsWith("main", StringComparison.OrdinalIgnoreCase) &&
                                e.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in bundles)
                {
                    using var reader = new StreamReader(entry.Open());
                    if (Find(reader.ReadToEnd()) is { } version)
                        return version;
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The version in a main bundle's text, or null when the expression is not in it.</summary>
        public static string? Find(string bundleText)
        {
            var match = InBundle.Match(bundleText ?? string.Empty);
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
