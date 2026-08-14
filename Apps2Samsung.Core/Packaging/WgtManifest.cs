using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads values from a <c>.wgt</c> package's <c>config.xml</c> that both heads need before install.
    /// Kept in Core so the desktop and mobile installers share one implementation instead of each
    /// parsing the manifest their own way.
    /// </summary>
    public static class WgtManifest
    {
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
