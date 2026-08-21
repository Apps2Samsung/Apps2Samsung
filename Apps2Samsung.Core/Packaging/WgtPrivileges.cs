using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads the Tizen privileges a package declares in its manifest and decides whether it needs a
    /// Partner-level distributor certificate. This is how the installer auto-selects the signing level
    /// without any per-package metadata: a package that needs a restricted API (e.g. VPN, DRM info)
    /// must declare the matching privilege to work, so the declaration itself is the source of truth.
    /// Handles both package shapes — web apps (.wgt, config.xml) and .NET/native apps (.tpk,
    /// tizen-manifest.xml).
    /// </summary>
    public static class WgtPrivileges
    {
        // Privileges that are only granted to Partner-signed apps. Extend as more are needed.
        private static readonly HashSet<string> PartnerPrivileges = new(StringComparer.OrdinalIgnoreCase)
        {
            "http://tizen.org/privilege/vpnservice",
            // Samsung TV DRM: querying DRM *info* is Partner level while playback (drmplay) next to it
            // is Public — so the developer.samsung.com namespace can't be treated as Partner wholesale
            // and the Partner members are listed one by one. (Apps2Samsung/Overscan#13.)
            "http://developer.samsung.com/privilege/drminfo",
            "http://developer.samsung.com/privilege/sso.partner",
        };

        // Samsung names its partner-tier privileges with a ".partner" suffix (e.g. sso.partner), so a
        // package declaring one needs Partner signing even when it isn't listed above yet.
        private const string PartnerSuffix = ".partner";

        // .wgt / config.xml: <tizen:privilege name="http://tizen.org/privilege/..."/>
        private static readonly Regex WebPrivilege = new(
            @"<tizen:privilege\b[^>]*\bname\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // .tpk / tizen-manifest.xml: <privilege>http://tizen.org/privilege/...</privilege>
        private static readonly Regex NativePrivilege = new(
            @"<privilege>\s*(?<name>[^<\s][^<]*?)\s*</privilege>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The privilege names declared in the package's manifest (empty if unreadable).</summary>
        public static IReadOnlyList<string> ReadPrivileges(string packagePath)
        {
            var names = new List<string>();
            try
            {
                using var zip = ZipFile.OpenRead(packagePath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Equals("config.xml", StringComparison.OrdinalIgnoreCase))
                        AddMatches(entry, WebPrivilege, names);
                    else if (entry.Name.Equals("tizen-manifest.xml", StringComparison.OrdinalIgnoreCase))
                        AddMatches(entry, NativePrivilege, names);
                }
            }
            catch
            {
                // Unreadable archive/manifest — treat as declaring nothing.
            }
            return names;
        }

        /// <summary>True if the package declares a privilege that requires Partner-level signing.</summary>
        public static bool RequiresPartner(string packagePath) =>
            FindPartnerPrivilege(packagePath) is not null;

        /// <summary>
        /// The first Partner-only privilege the package declares, or <c>null</c> when Public signing is
        /// enough. Returned instead of a bare bool so callers can tell the user *why* the level was
        /// bumped (and log it when a TV still rejects the install).
        /// </summary>
        public static string? FindPartnerPrivilege(string packagePath) =>
            FindPartnerPrivilege(ReadPrivileges(packagePath));

        /// <summary>Same decision for privileges that were already read.</summary>
        public static string? FindPartnerPrivilege(IEnumerable<string> privileges) =>
            privileges.FirstOrDefault(IsPartnerPrivilege);

        /// <summary>True if this single privilege is only granted to Partner-signed apps.</summary>
        public static bool IsPartnerPrivilege(string privilege) =>
            !string.IsNullOrWhiteSpace(privilege) &&
            (PartnerPrivileges.Contains(privilege.Trim()) ||
             privilege.TrimEnd().EndsWith(PartnerSuffix, StringComparison.OrdinalIgnoreCase));

        private static void AddMatches(ZipArchiveEntry entry, Regex regex, List<string> into)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            foreach (Match m in regex.Matches(xml))
                into.Add(m.Groups["name"].Value.Trim());
        }
    }
}
