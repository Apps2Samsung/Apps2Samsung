using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads the Tizen privileges a <c>.wgt</c> declares in its <c>config.xml</c> and decides whether
    /// it needs a Partner-level distributor certificate. This is how the installer auto-selects the
    /// signing level without any per-package metadata: a package that needs a restricted API (e.g. VPN)
    /// must declare the matching privilege to work, so the declaration itself is the source of truth.
    /// </summary>
    public static class WgtPrivileges
    {
        // Tizen privileges that are only granted to Partner-signed apps. Extend as more are needed.
        private static readonly HashSet<string> PartnerPrivileges = new(StringComparer.OrdinalIgnoreCase)
        {
            "http://tizen.org/privilege/vpnservice",
        };

        private static readonly Regex PrivilegeName = new(
            @"<tizen:privilege\b[^>]*\bname\s*=\s*""(?<name>[^""]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The privilege names declared in the package's config.xml (empty if unreadable).</summary>
        public static IReadOnlyList<string> ReadPrivileges(string wgtPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(wgtPath);
                var entry = zip.GetEntry("config.xml");
                if (entry is null)
                    return Array.Empty<string>();

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var xml = reader.ReadToEnd();

                var names = new List<string>();
                foreach (Match m in PrivilegeName.Matches(xml))
                    names.Add(m.Groups["name"].Value.Trim());
                return names;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>True if the package declares a privilege that requires Partner-level signing.</summary>
        public static bool RequiresPartner(string wgtPath) =>
            ReadPrivileges(wgtPath).Any(PartnerPrivileges.Contains);
    }
}
