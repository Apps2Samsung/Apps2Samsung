using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Certificate
{
    /// <summary>What one signing profile in an archive (or in a store) looks like, read without installing it.</summary>
    /// <param name="ProfileName">The profile folder, e.g. "Jelly2Sams - Public".</param>
    /// <param name="Level">Public or Partner when the folder is one of ours, null for a custom profile.</param>
    /// <param name="AuthorSubject">The author certificate's subject, e.g. "CN=Jelly2Sams".</param>
    /// <param name="AccountEmail">The Samsung account the distributor certificate was issued to, if it says.</param>
    /// <param name="State">The author certificate against the clock.</param>
    /// <param name="NotBeforeUtc">Author validity start, null when unreadable.</param>
    /// <param name="NotAfterUtc">Author validity end, null when unreadable.</param>
    /// <param name="Duids">The TVs the distributor certificate covers.</param>
    /// <param name="FileCount">Files in the profile folder.</param>
    public sealed record CertificateArchiveProfile(
        string ProfileName,
        CertificatePrivilegeLevel? Level,
        string? AuthorSubject,
        string? AccountEmail,
        CertificateValidityState State,
        DateTime? NotBeforeUtc,
        DateTime? NotAfterUtc,
        IReadOnlyList<string> Duids,
        int FileCount)
    {
        /// <summary>True when the author certificate loads and has not expired.</summary>
        public bool IsUsable => State is CertificateValidityState.Valid or CertificateValidityState.NotYetValid;

        /// <summary>Whether the distributor certificate covers <paramref name="duid"/>.</summary>
        public bool Covers(string? duid) =>
            !string.IsNullOrEmpty(duid) && Duids.Contains(duid, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What an archive holds, and what importing it would replace on this device.</summary>
    /// <param name="Profiles">The profiles in the archive.</param>
    /// <param name="Replaces">The profiles on this device with the same name, i.e. what the import overwrites.</param>
    public sealed record CertificateArchivePreview(
        IReadOnlyList<CertificateArchiveProfile> Profiles,
        IReadOnlyList<CertificateArchiveProfile> Replaces)
    {
        /// <summary>True when the archive holds nothing that can be imported.</summary>
        public bool IsEmpty => Profiles.Count == 0;

        /// <summary>The profiles an import would refuse: author certificate expired or unreadable.</summary>
        public IEnumerable<CertificateArchiveProfile> Rejected => Profiles.Where(p => !p.IsUsable);

        /// <summary>True when at least one archive profile covers <paramref name="duid"/>.</summary>
        public bool Covers(string? duid) => Profiles.Any(p => p.IsUsable && p.Covers(duid));
    }

    /// <summary>The outcome of <see cref="CertificateArchive.Import"/>.</summary>
    /// <param name="Imported">The profiles written.</param>
    /// <param name="FileCount">Files written in total.</param>
    public sealed record CertificateArchiveImportResult(IReadOnlyList<CertificateArchiveProfile> Imported, int FileCount);

    /// <summary>An archive that cannot be imported, with a reason the user can read.</summary>
    public sealed class CertificateArchiveException : Exception
    {
        public CertificateArchiveException(string message) : base(message) { }
    }

    /// <summary>
    /// The signing certificate on its own, as one file, so a user who signed in once on one head can
    /// carry that profile to the phone, the iPhone, the CLI or another computer without a second
    /// Samsung sign-in, and without the TV ending up with apps signed by two author certificates
    /// (the "118 -22" trap when a second head provisions its own, #921).
    /// <para>
    /// The archive is a zip with the <c>Certificate/&lt;Profile&gt;/…</c> layout of #510's full backup
    /// and nothing else: no <c>settings.json</c>, so importing it changes no setting on the other head.
    /// A full backup zip therefore also reads as a certificate archive (its settings are ignored).
    /// </para>
    /// </summary>
    public static class CertificateArchive
    {
        /// <summary>The archive's own extension, so a file picker can target it.</summary>
        public const string FileExtension = ".a2scert";

        /// <summary>The default file name for an export.</summary>
        public const string SuggestedFileName = "apps2samsung-certificate" + FileExtension;

        private const string CertPrefix = "Certificate/";

        /// <summary>
        /// Writes the profile(s) in <paramref name="storePath"/> into <paramref name="zipOutput"/>:
        /// one level, or every usable level when <paramref name="level"/> is null. The stream is left
        /// open. Throws <see cref="CertificateArchiveException"/> when there is nothing usable to export.
        /// </summary>
        public static IReadOnlyList<CertificateArchiveProfile> Export(Stream zipOutput, string storePath, CertificatePrivilegeLevel? level = null)
        {
            var levels = level is { } one ? new[] { one } : new[] { CertificatePrivilegeLevel.Public, CertificatePrivilegeLevel.Partner };
            var exported = new List<CertificateArchiveProfile>();

            using var zip = new ZipArchive(zipOutput, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var each in levels)
            {
                var name = CertificateProvisioningService.ProfileName(each);
                var dir = Path.Combine(storePath, name);
                var profile = DescribeDirectory(dir);
                if (profile is null || !profile.IsUsable)
                    continue;

                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, CertPrefix + name + "/" + rel, CompressionLevel.Optimal);
                }

                exported.Add(profile);
            }

            if (exported.Count == 0)
                throw new CertificateArchiveException("There is no usable signing certificate to export.");

            return exported;
        }

        /// <summary>
        /// Reads the archive without writing anything: which profiles it holds, who they were issued
        /// to, how long they are valid, which TVs they cover, and which profiles in
        /// <paramref name="storePath"/> an import would replace.
        /// </summary>
        public static CertificateArchivePreview Preview(Stream zipInput, string storePath)
        {
            var profiles = ReadProfiles(zipInput).Select(p => p.Profile).ToList();
            var replaces = profiles
                .Select(p => DescribeDirectory(Path.Combine(storePath, p.ProfileName)))
                .Where(existing => existing is not null)
                .Select(existing => existing!)
                .ToList();
            return new CertificateArchivePreview(profiles, replaces);
        }

        /// <summary>
        /// Writes the archive's profiles into <paramref name="storePath"/>, replacing a profile of the
        /// same name whole (never a mix of an old distributor with a new author). An author
        /// certificate that has expired, or does not load, is refused with
        /// <see cref="CertificateArchiveException"/> before anything is written.
        /// </summary>
        public static CertificateArchiveImportResult Import(Stream zipInput, string storePath)
        {
            var read = ReadProfiles(zipInput);
            if (read.Count == 0)
                throw new CertificateArchiveException("The file holds no signing certificate (no author.p12, distributor.p12 and password.txt in a Certificate/<profile>/ folder).");

            var rejected = read.Select(r => r.Profile).FirstOrDefault(p => !p.IsUsable);
            if (rejected is not null)
            {
                throw new CertificateArchiveException(rejected.State == CertificateValidityState.Expired
                    ? $"The {rejected.ProfileName} author certificate expired on {rejected.NotAfterUtc:yyyy-MM-dd}; an expired certificate cannot sign anything, so it is not imported. Sign in with Samsung once to get a new one."
                    : $"The {rejected.ProfileName} author certificate could not be read (wrong password.txt or a damaged file), so it is not imported.");
            }

            var storeRoot = Path.GetFullPath(storePath);
            var files = 0;
            foreach (var (profile, entries) in read)
            {
                var dir = Path.GetFullPath(Path.Combine(storePath, profile.ProfileName));
                if (!dir.StartsWith(storeRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);

                foreach (var (relative, content) in entries)
                {
                    var dest = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
                    // Zip-slip guard: never write outside the profile folder.
                    if (!dest.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, content);
                    files++;
                }
            }

            return new CertificateArchiveImportResult(read.Select(r => r.Profile).ToList(), files);
        }

        /// <summary>
        /// The certificate preference after an import, as the mobile head has done since #510: the
        /// imported level when exactly one level is now usable on this device, else unchanged.
        /// </summary>
        public static string PreferenceAfterImport(string? current, string storePath) =>
            CertificatePreference.AfterImport(
                current,
                hasPublic: CertificateProvisioningService.HasProfile(storePath, CertificatePrivilegeLevel.Public),
                hasPartner: CertificateProvisioningService.HasProfile(storePath, CertificatePrivilegeLevel.Partner));

        /// <summary>The profile in a store folder, or null when the folder is not a complete profile.</summary>
        public static CertificateArchiveProfile? DescribeDirectory(string profileDir)
        {
            if (!Directory.Exists(profileDir))
                return null;

            byte[]? Read(string file)
            {
                var path = Path.Combine(profileDir, file);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }

            var count = Directory.EnumerateFiles(profileDir, "*", SearchOption.AllDirectories).Count();
            return Describe(Path.GetFileName(profileDir), Read(Constants.Certificate.AuthorFileName),
                Read(Constants.Certificate.DistributorFileName), Read(Constants.Certificate.PasswordFileName), count);
        }

        /// <summary>
        /// A readable summary of a preview for a confirmation dialog, built from the heads' strings.
        /// <paramref name="selectedDuid"/> adds whether the selected TV is covered; null leaves that out.
        /// </summary>
        public static string Summarize(CertificateArchivePreview preview, string? selectedDuid, Func<string, string> localize)
        {
            var lines = new List<string>();
            if (preview.IsEmpty)
            {
                lines.Add(localize("certArchiveNoProfiles"));
                return string.Join("\n", lines);
            }

            foreach (var p in preview.Profiles)
            {
                var who = p.AccountEmail ?? p.AuthorSubject ?? "?";
                if (p.State == CertificateValidityState.Expired)
                    lines.Add(string.Format(localize("certArchiveExpired"), LevelLabel(p, localize), who, Day(p.NotAfterUtc)));
                else if (p.State == CertificateValidityState.Unreadable)
                    lines.Add(string.Format(localize("certArchiveUnreadable"), LevelLabel(p, localize)));
                else
                    lines.Add(string.Format(localize("certArchiveProfileLine"), LevelLabel(p, localize), who, Day(p.NotBeforeUtc), Day(p.NotAfterUtc), p.Duids.Count));
            }

            if (!string.IsNullOrEmpty(selectedDuid))
                lines.Add(localize(preview.Covers(selectedDuid) ? "certArchiveCoversThisTv" : "certArchiveNotCoversThisTv"));

            foreach (var existing in preview.Replaces)
                lines.Add(string.Format(localize("certArchiveReplaces"), LevelLabel(existing, localize), existing.AccountEmail ?? existing.AuthorSubject ?? "?", Day(existing.NotAfterUtc)));

            return string.Join("\n", lines);
        }

        private static string LevelLabel(CertificateArchiveProfile p, Func<string, string> localize) => p.Level switch
        {
            CertificatePrivilegeLevel.Partner => localize("certLevelPartner"),
            CertificatePrivilegeLevel.Public => localize("certLevelPublic"),
            _ => p.ProfileName,
        };

        private static string Day(DateTime? utc) => utc is { } d ? d.ToString("yyyy-MM-dd") : "?";

        // Every complete profile in the archive, with its files, in archive order.
        private static List<(CertificateArchiveProfile Profile, List<(string Relative, byte[] Content)> Entries)> ReadProfiles(Stream zipInput)
        {
            var byProfile = new Dictionary<string, List<(string Relative, byte[] Content)>>(StringComparer.Ordinal);
            var order = new List<string>();

            using var zip = new ZipArchive(zipInput, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(CertPrefix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(entry.Name))
                    continue;

                var rel = entry.FullName.Substring(CertPrefix.Length);
                var slash = rel.IndexOf('/');
                if (slash <= 0 || slash == rel.Length - 1)
                    continue; // a file straight under Certificate/ is not part of a profile

                var profileName = rel.Substring(0, slash);
                if (profileName is "." or ".." || profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    continue;

                if (!byProfile.TryGetValue(profileName, out var files))
                {
                    files = new List<(string, byte[])>();
                    byProfile[profileName] = files;
                    order.Add(profileName);
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                files.Add((rel.Substring(slash + 1), buffer.ToArray()));
            }

            var result = new List<(CertificateArchiveProfile, List<(string, byte[])>)>();
            foreach (var name in order)
            {
                var files = byProfile[name];
                byte[]? Of(string file) => files.FirstOrDefault(f => string.Equals(f.Relative, file, StringComparison.OrdinalIgnoreCase)).Content;
                var profile = Describe(name, Of(Constants.Certificate.AuthorFileName), Of(Constants.Certificate.DistributorFileName), Of(Constants.Certificate.PasswordFileName), files.Count);
                if (profile is not null)
                    result.Add((profile, files));
            }

            return result;
        }

        // A profile from its three defining files; null when one is missing (then it is no profile).
        private static CertificateArchiveProfile? Describe(string name, byte[]? author, byte[]? distributor, byte[]? passwordFile, int fileCount)
        {
            if (author is null || distributor is null || passwordFile is null)
                return null;

            var password = Encoding.UTF8.GetString(passwordFile).Trim();
            var level = LevelOf(name);

            string? subject = null;
            CertificateValidityState state;
            DateTime? notBefore = null, notAfter = null;
            try
            {
#pragma warning disable SYSLIB0057 // matches the loader used elsewhere; legacy PKCS#12 support
                using var cert = new X509Certificate2(author, password, PlatformService.SigningCertificateFlags);
#pragma warning restore SYSLIB0057
                subject = cert.Subject;
                notBefore = cert.NotBefore.ToUniversalTime();
                notAfter = cert.NotAfter.ToUniversalTime();
                var now = DateTime.UtcNow;
                state = notBefore > now ? CertificateValidityState.NotYetValid
                    : notAfter < now ? CertificateValidityState.Expired
                    : CertificateValidityState.Valid;
            }
            catch
            {
                state = CertificateValidityState.Unreadable;
            }

            string? email = null;
            var duids = new List<string>();
            try
            {
#pragma warning disable SYSLIB0057
                using var dist = new X509Certificate2(distributor, password, PlatformService.SigningCertificateFlags);
#pragma warning restore SYSLIB0057
                var mail = dist.GetNameInfo(X509NameType.EmailName, forIssuer: false);
                email = string.IsNullOrWhiteSpace(mail) ? null : mail;
                duids = CertificateHelper.DuidsOf(dist).OrderBy(d => d, StringComparer.Ordinal).ToList();
            }
            catch
            {
                // Reported with no account and no TVs; the author verdict above still stands.
            }

            return new CertificateArchiveProfile(name, level, subject, email, state, notBefore, notAfter, duids, fileCount);
        }

        private static CertificatePrivilegeLevel? LevelOf(string profileName)
        {
            foreach (var level in new[] { CertificatePrivilegeLevel.Public, CertificatePrivilegeLevel.Partner })
            {
                if (string.Equals(profileName, CertificateProvisioningService.ProfileName(level), StringComparison.OrdinalIgnoreCase))
                    return level;
            }
            return null;
        }
    }
}
