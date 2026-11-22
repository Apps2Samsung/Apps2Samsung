using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Apps2Samsung.Certificate
{
    /// <summary>Which set of certificate authorities: the three a Samsung certificate chains to, or the one a Tizen certificate is signed with.</summary>
    public enum AuthoritySet
    {
        Samsung,
        Tizen,
    }

    /// <summary>One authority file and the day its certificate ends.</summary>
    public sealed record AuthorityFile(string FileName, DateTimeOffset EndsAt);

    /// <summary>A set of authorities fetched from download.tizen.org: file name to contents. The Tizen set carries its key as a second file.</summary>
    public sealed record AuthorityDownload(AuthoritySet Set, IReadOnlyDictionary<string, byte[]> Files);

    /// <summary>
    /// The certificate authorities Apps2Samsung ships have end dates (#1153): the Tizen Developers CA
    /// ends on 2027-01-01, Samsung's VD developer authorities in 2029 and 2040. The app keeps
    /// shipping them, reads the dates itself, and when one has ended can fetch what Samsung and
    /// Tizen publish now, from the same two places Tizen Studio gets them:
    /// <list type="bullet">
    /// <item>Samsung: <c>extension_info.xml</c> names the "Samsung Certificate Extension" archive;
    /// in it an add-on zip per OS, in that the certificate manager's <c>org.tizen.common.cert</c>
    /// plugin, and in that <c>res/ca/</c>.</item>
    /// <item>Tizen: <c>pkg_list_&lt;os&gt;</c> names the <c>certificate-generator</c> package, which
    /// holds <c>certificates/developer/tizen-developer-ca.cer</c> and its key.</item>
    /// </list>
    /// Nothing here decides or asks; a head does, with <see cref="Ended"/> for the question and
    /// <see cref="IsUsableReplacement"/> for what came back.
    /// </summary>
    public static class CertificateAuthorities
    {
        public const string ExtensionInfoUrl = "https://download.tizen.org/sdk/tizenstudio/official/extension_info.xml";
        public const string PackageListUrl = "https://download.tizen.org/sdk/tizenstudio/official/pkg_list_ubuntu-64";
        public const string PackageBaseUrl = "https://download.tizen.org/sdk/tizenstudio/official";

        public const string SamsungAuthorCa = "vd_tizen_dev_author_ca.cer";
        public const string SamsungPublicCa = "vd_tizen_dev_public2.crt";
        public const string SamsungPartnerCa = "vd_tizen_dev_partner2.crt";
        public const string TizenDeveloperCa = "tizen-developer-ca.cer";
        public const string TizenDeveloperCaKey = "tizen-developer-ca-privatekey.pem";

        /// <summary>The files of the Samsung set, as the certificate service looks for them in its authority folder.</summary>
        public static readonly IReadOnlyList<string> SamsungFiles = new[] { SamsungAuthorCa, SamsungPublicCa, SamsungPartnerCa };

        /// <summary>A certificate from its file: PEM (also with openssl's text dump in front of it) or DER.</summary>
        public static X509Certificate2 Read(byte[] file)
        {
            var text = Encoding.ASCII.GetString(file);
            int begin = text.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
            return begin >= 0
                ? X509Certificate2.CreateFromPem(text.AsSpan(begin))
                : X509CertificateLoader.LoadCertificate(file);
        }

        /// <summary>When the certificate in <paramref name="file"/> ends; null when it cannot be read.</summary>
        public static DateTimeOffset? EndsAt(byte[] file)
        {
            try
            {
                using var certificate = Read(file);
                return new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The authorities among <paramref name="files"/> that have ended by <paramref name="now"/>,
        /// the one that ended first in front. A file that cannot be read is left out: a broken file
        /// fails loudly where it is used, it is not an end date.
        /// </summary>
        public static IReadOnlyList<AuthorityFile> Ended(IEnumerable<KeyValuePair<string, byte[]>> files, DateTimeOffset now) =>
            files.Select(f => (f.Key, Ends: EndsAt(f.Value)))
                .Where(f => f.Ends is { } ends && ends <= now)
                .Select(f => new AuthorityFile(f.Key, f.Ends!.Value))
                .OrderBy(f => f.EndsAt)
                .ToList();

        /// <summary>
        /// True when a downloaded authority may take the place of the one in use: it is a readable
        /// certificate, it has not ended, and it ends later than the current one. Anything else
        /// (a damaged download, the same file again, an older one) is not a replacement.
        /// </summary>
        public static bool IsUsableReplacement(byte[] candidate, byte[]? current, DateTimeOffset now)
        {
            if (EndsAt(candidate) is not { } ends || ends <= now)
                return false;
            try
            {
                using var certificate = Read(candidate);
                if (new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero) > now)
                    return false;
            }
            catch (Exception)
            {
                return false;
            }
            return current is null || EndsAt(current) is not { } currentEnds || ends > currentEnds;
        }

        /// <summary>The archive <c>extension_info.xml</c> names for the Samsung Certificate Extension; null when it names none.</summary>
        public static string? SamsungExtensionUrl(string extensionInfoXml)
        {
            try
            {
                var repository = XDocument.Parse(extensionInfoXml)
                    .Descendants("extension")
                    .FirstOrDefault(e => string.Equals(e.Element("name")?.Value.Trim(), "Samsung Certificate Extension", StringComparison.OrdinalIgnoreCase))
                    ?.Element("repository")?.Value.Trim();
                return Uri.TryCreate(repository, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && IsTizenHost(uri)
                    ? uri.ToString()
                    : null;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        /// <summary>The <c>certificate-generator</c> package a package list names, as a URL; null when it names none.</summary>
        public static string? TizenGeneratorUrl(string packageList)
        {
            foreach (var block in Regex.Split(packageList.Replace("\r\n", "\n"), @"\n\s*\n"))
            {
                if (!Regex.IsMatch(block, @"^Package\s*:\s*certificate-generator\s*$", RegexOptions.Multiline))
                    continue;
                var path = Regex.Match(block, @"^Path\s*:\s*(\S+)\s*$", RegexOptions.Multiline);
                // A plain path under the package base: nothing that climbs out of it or names another host.
                if (path.Success && Regex.IsMatch(path.Groups[1].Value, @"^/binary/[A-Za-z0-9._-]+\.zip$"))
                    return PackageBaseUrl + path.Groups[1].Value;
            }
            return null;
        }

        private static bool IsTizenHost(Uri uri) =>
            string.Equals(uri.Host, "download.tizen.org", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The three Samsung authorities out of the extension archive: add-on zip, plugin jar,
        /// <c>res/ca/</c>. Null when the archive does not hold all three.
        /// </summary>
        public static AuthorityDownload? ExtractSamsung(Stream extensionArchive)
        {
            using var outer = new ZipArchive(extensionArchive, ZipArchiveMode.Read, leaveOpen: true);
            // Every OS's add-on carries the same authorities; the first is as good as any.
            var addOn = outer.Entries
                .Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (addOn is null)
                return null;

            using var addOnBytes = Buffer(addOn);
            using var inner = new ZipArchive(addOnBytes, ZipArchiveMode.Read);
            var plugin = inner.Entries
                .Where(e => e.Name.StartsWith("org.tizen.common.cert_", StringComparison.OrdinalIgnoreCase)
                            && e.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Contains("certificate-manager", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (plugin is null)
                return null;

            using var pluginBytes = Buffer(plugin);
            using var jar = new ZipArchive(pluginBytes, ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SamsungFiles)
            {
                var entry = jar.GetEntry("res/ca/" + name);
                if (entry is null)
                    return null;
                using var bytes = Buffer(entry);
                files[name] = bytes.ToArray();
            }
            return new AuthorityDownload(AuthoritySet.Samsung, files);
        }

        /// <summary>The Tizen Developers CA and its key out of the certificate-generator package. Null when either is missing.</summary>
        public static AuthorityDownload? ExtractTizen(Stream generatorPackage)
        {
            using var zip = new ZipArchive(generatorPackage, ZipArchiveMode.Read, leaveOpen: true);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { TizenDeveloperCa, TizenDeveloperCaKey })
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("certificates/developer/" + name, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    return null;
                using var bytes = Buffer(entry);
                files[name] = bytes.ToArray();
            }
            return new AuthorityDownload(AuthoritySet.Tizen, files);
        }

        // An entry in memory: a nested archive has to be seekable to be opened.
        private static MemoryStream Buffer(ZipArchiveEntry entry)
        {
            // The largest real file here is a 15 MB add-on; anything far beyond that is not what we asked for.
            if (entry.Length > 64 * 1024 * 1024)
                throw new InvalidDataException($"'{entry.FullName}' is larger than an authority archive can be.");
            var memory = new MemoryStream();
            using (var stream = entry.Open())
                stream.CopyTo(memory);
            memory.Position = 0;
            return memory;
        }

        /// <summary>
        /// Fetches the set Samsung or Tizen publishes now. Null when the server names no archive or
        /// the archive does not hold the files; a network failure throws, for the head to report.
        /// </summary>
        public static async Task<AuthorityDownload?> DownloadAsync(HttpClient http, AuthoritySet set, CancellationToken cancellationToken = default)
        {
            string? url = set == AuthoritySet.Samsung
                ? SamsungExtensionUrl(await http.GetStringAsync(ExtensionInfoUrl, cancellationToken).ConfigureAwait(false))
                : TizenGeneratorUrl(await http.GetStringAsync(PackageListUrl, cancellationToken).ConfigureAwait(false));
            if (url is null)
                return null;

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var archive = new MemoryStream();
            await response.Content.CopyToAsync(archive, cancellationToken).ConfigureAwait(false);
            archive.Position = 0;
            try
            {
                return set == AuthoritySet.Samsung ? ExtractSamsung(archive) : ExtractTizen(archive);
            }
            catch (InvalidDataException)
            {
                // A damaged download (antivirus web scanning does this): the same as nothing usable.
                return null;
            }
        }
    }
}
