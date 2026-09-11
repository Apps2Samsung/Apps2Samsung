using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Reads the Tizen privileges a package declares in its manifest and decides whether it needs a
    /// Partner-level distributor certificate. This is how the installer auto-selects the signing level
    /// without any per-package metadata: a package that needs a restricted API (e.g. VPN, DRM info)
    /// must declare the matching privilege to work, so the declaration itself is the source of truth.
    /// The same goes for the launch settings <c>on-boot="true"</c> and <c>auto-restart="true"</c>:
    /// Tizen only honours them for Partner-signed apps, so a manifest that sets them needs Partner
    /// signing even when it declares no restricted privilege at all.
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

        // Launch settings that Tizen only honours for Partner-signed apps. They are attributes on the
        // application element in both manifest shapes — <tizen:application .../> or <tizen:service .../>
        // in config.xml, <ui-application .../> or <service-application .../> in tizen-manifest.xml —
        // so a single attribute match covers both. Only an explicit "true" counts; "false" (or the
        // attribute being absent) is the Public default.
        private static readonly Regex PartnerLaunchSetting = new(
            @"\b(?<name>on-boot|auto-restart)\s*=\s*""\s*true\s*""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Commented-out markup must not trigger either check.
        private static readonly Regex XmlComment = new(
            @"<!--.*?-->",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>The privilege names declared in the package's manifest (empty if unreadable).</summary>
        public static IReadOnlyList<string> ReadPrivileges(string packagePath)
        {
            var names = new List<string>();
            ForEachManifest(packagePath, (isWebManifest, xml) =>
                AddMatches(xml, isWebManifest ? WebPrivilege : NativePrivilege, names));
            return names;
        }

        /// <summary>
        /// The Partner-only launch settings the package's manifest turns on, as <c>on-boot="true"</c> /
        /// <c>auto-restart="true"</c> (empty if none, or if the package is unreadable).
        /// </summary>
        public static IReadOnlyList<string> ReadPartnerLaunchSettings(string packagePath)
        {
            var settings = new List<string>();
            ForEachManifest(packagePath, (_, xml) =>
            {
                foreach (Match m in PartnerLaunchSetting.Matches(xml))
                    settings.Add($"{m.Groups["name"].Value.ToLowerInvariant()}=\"true\"");
            });
            return settings;
        }

        /// <summary>True if the package declares a privilege or launch setting that requires Partner-level signing.</summary>
        public static bool RequiresPartner(string packagePath) =>
            FindPartnerPrivilege(packagePath) is not null;

        /// <summary>
        /// The first Partner-only requirement the package declares — a Partner privilege name, or a
        /// launch setting such as <c>on-boot="true"</c> — or <c>null</c> when Public signing is enough.
        /// Returned instead of a bare bool so callers can tell the user *why* the level was bumped (and
        /// log it when a TV still rejects the install).
        /// </summary>
        public static string? FindPartnerPrivilege(string packagePath) =>
            FindPartnerPrivilege(ReadPrivileges(packagePath))
            ?? ReadPartnerLaunchSettings(packagePath).FirstOrDefault();

        /// <summary>Same decision for privileges that were already read.</summary>
        public static string? FindPartnerPrivilege(IEnumerable<string> privileges) =>
            privileges.FirstOrDefault(IsPartnerPrivilege);

        /// <summary>True if this single privilege is only granted to Partner-signed apps.</summary>
        public static bool IsPartnerPrivilege(string privilege) =>
            !string.IsNullOrWhiteSpace(privilege) &&
            (PartnerPrivileges.Contains(privilege.Trim()) ||
             privilege.TrimEnd().EndsWith(PartnerSuffix, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Runs <paramref name="visit"/> over every manifest in the package (config.xml → web,
        /// tizen-manifest.xml → native) with XML comments already stripped. An unreadable archive or
        /// manifest is treated as declaring nothing.
        /// </summary>
        private static void ForEachManifest(string packagePath, Action<bool, string> visit)
        {
            try
            {
                using var zip = ZipFile.OpenRead(packagePath);
                foreach (var entry in zip.Entries)
                {
                    bool isWebManifest = entry.Name.Equals("config.xml", StringComparison.OrdinalIgnoreCase);
                    bool isNativeManifest = entry.Name.Equals("tizen-manifest.xml", StringComparison.OrdinalIgnoreCase);
                    if (!isWebManifest && !isNativeManifest)
                        continue;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var xml = XmlComment.Replace(reader.ReadToEnd(), string.Empty);
                    visit(isWebManifest, xml);
                }
            }
            catch
            {
                // Unreadable archive/manifest — treat as declaring nothing.
            }
        }

        private static void AddMatches(string xml, Regex regex, List<string> into)
        {
            foreach (Match m in regex.Matches(xml))
                into.Add(m.Groups["name"].Value.Trim());
        }
    }
}
