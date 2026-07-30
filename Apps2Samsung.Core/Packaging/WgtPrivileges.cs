using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads the Tizen privileges a package declares in its manifest and decides whether it needs a
    /// Partner-level distributor certificate. This is how the installer auto-selects the signing level
    /// without any per-package metadata: a package that needs a restricted API (e.g. VPN) must declare
    /// the matching privilege to work, so the declaration itself is the source of truth. Handles both
    /// package shapes — web apps (.wgt, config.xml) and .NET/native apps (.tpk, tizen-manifest.xml).
    /// </summary>
    public static class WgtPrivileges
    {
        // Tizen privileges that are only granted to Partner-signed apps. Extend as more are needed.
        private static readonly HashSet<string> PartnerPrivileges = new(StringComparer.OrdinalIgnoreCase)
        {
            "http://tizen.org/privilege/vpnservice",
        };

        // Partner-only / Samsung-restricted privileges that a PUBLIC-signed package must not declare on
        // old firmware: pre-Tizen-4 TVs reject the whole install with a generic install failed[118]
        // when a public cert requests one (#400 — LiteFin declares SmartHub 'preview'). Newer TVs just
        // ignore an ungranted privilege, and Partner-signed installs are entitled to them, so these are
        // only stripped for a public install on an old TV — never when Partner signing is in effect.
        // Deliberately NOT in PartnerPrivileges: adding them there would force Partner signing, which
        // retail TVs/individual accounts often can't obtain — the point is to install *public*.
        public static readonly IReadOnlyCollection<string> PublicIncompatiblePrivileges =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://developer.samsung.com/privilege/preview",
        };

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
            ReadPrivileges(packagePath).Any(PartnerPrivileges.Contains);

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
