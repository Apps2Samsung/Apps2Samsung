using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// A signing profile as PEM text: what the TV-Store app on a TV signs packages with (#844).
    /// Node on the TV has no PKCS#12 parser, so the head, which holds the .p12 files and their
    /// password, opens them and hands over the key and the certificate chain as text.
    /// </summary>
    /// <param name="AuthorKeyPem">The author private key, PKCS#8 ("PRIVATE KEY").</param>
    /// <param name="AuthorCertPem">
    /// The author certificate chain: the leaf first, then its intermediates, never the root. One
    /// CERTIFICATE block per certificate, concatenated. The same chain the C# signer puts into
    /// KeyInfo, so a package signed on the TV comes out as one signed from a PC.
    /// </param>
    /// <param name="DistributorKeyPem">The distributor private key, PKCS#8.</param>
    /// <param name="DistributorCertPem">The distributor chain, as <paramref name="AuthorCertPem"/>.</param>
    /// <param name="Name">The profile's folder name ("Jelly2Sams - Public", "Jellyfin"), what TV-Store shows and logs.</param>
    public sealed record SigningCertificatePem(
        string AuthorKeyPem,
        string AuthorCertPem,
        string DistributorKeyPem,
        string DistributorCertPem,
        string? Name = null)
    {
        /// <summary>Opens both .p12 files of <paramref name="profile"/> and renders them as PEM.</summary>
        /// <exception cref="CryptographicException">A .p12 would not open with the profile's password, or a key is not exportable.</exception>
        /// <exception cref="InvalidOperationException">A .p12 carries no certificate with a private key.</exception>
        public static SigningCertificatePem FromProfile(SigningProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var (authorKey, authorChain) = Open(profile.AuthorP12, profile.Password);
            var (distributorKey, distributorChain) = Open(profile.DistributorP12, profile.Password);
            return new SigningCertificatePem(authorKey, authorChain, distributorKey, distributorChain, ProfileNameOf(profile));
        }

        /// <summary>
        /// Every signing profile under the given stores: each folder holding author.p12, distributor.p12
        /// and password.txt with an author certificate that still opens. The level profiles, imported
        /// ones and a head's bundled one, whatever the caller points at. Folder name order within a store.
        /// </summary>
        public static IReadOnlyList<SigningProfile> ProfilesIn(params string?[] stores)
        {
            var list = new List<SigningProfile>();
            foreach (var store in stores)
            {
                if (string.IsNullOrWhiteSpace(store) || !Directory.Exists(store))
                    continue;
                foreach (var dir in Directory.GetDirectories(store).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var author = Path.Combine(dir, Constants.Certificate.AuthorFileName);
                    var distributor = Path.Combine(dir, Constants.Certificate.DistributorFileName);
                    var passwordFile = Path.Combine(dir, Constants.Certificate.PasswordFileName);
                    if (!File.Exists(distributor) || !File.Exists(passwordFile) || !CertificateProvisioningService.HasUsableAuthorCert(dir))
                        continue;
                    list.Add(new SigningProfile(author, distributor, File.ReadAllText(passwordFile).Trim(), dir));
                }
            }
            return list;
        }

        /// <summary>
        /// What Arm TV-Store hands over (#44): <paramref name="primary"/> first (the profile an install
        /// on that TV would sign with), then every other profile in <paramref name="others"/>, so
        /// TV-Store can update an app with whichever author certificate it was installed with.
        /// Profiles whose .p12 will not open are skipped and traced; one that repeats the primary's
        /// folder or author certificate is dropped. The primary itself must open.
        /// </summary>
        public static IReadOnlyList<SigningCertificatePem> OpenAll(SigningProfile primary, IEnumerable<SigningProfile> others)
        {
            ArgumentNullException.ThrowIfNull(primary);
            ArgumentNullException.ThrowIfNull(others);
            var first = FromProfile(primary);
            var list = new List<SigningCertificatePem> { first };
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(primary.ProfileDir) };
            var seenAuthors = new HashSet<string>(StringComparer.Ordinal) { AuthorLeaf(first.AuthorCertPem) };
            foreach (var profile in others)
            {
                if (!seenDirs.Add(Path.GetFullPath(profile.ProfileDir)))
                    continue;
                SigningCertificatePem pem;
                try
                {
                    pem = FromProfile(profile);
                }
                catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IOException)
                {
                    System.Diagnostics.Trace.WriteLine($"[tvstore] skipping profile {ProfileNameOf(profile)}: {ex.Message}");
                    continue;
                }
                if (seenAuthors.Add(AuthorLeaf(pem.AuthorCertPem)))
                    list.Add(pem);
            }
            return list;
        }

        private static string ProfileNameOf(SigningProfile profile) =>
            Path.GetFileName(profile.ProfileDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // The first CERTIFICATE block of a chain, whitespace stripped: the author leaf, what the TV matches on.
        private static string AuthorLeaf(string chainPem)
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var start = chainPem.IndexOf(begin, StringComparison.Ordinal);
            var stop = start < 0 ? -1 : chainPem.IndexOf(end, start, StringComparison.Ordinal);
            if (start < 0 || stop < 0)
                return chainPem;
            return new string(chainPem.Substring(start + begin.Length, stop - start - begin.Length).Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        /// <summary>
        /// The signing profile under <paramref name="certificateStore"/> whose distributor certificate
        /// covers <paramref name="duid"/>: Public first, then Partner. Null when neither does, which
        /// means nothing has been installed on that TV from this machine yet (an install makes one).
        /// </summary>
        public static SigningProfile? FindProfileCovering(string certificateStore, string duid)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(certificateStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(duid);

            foreach (var level in new[] { CertificatePrivilegeLevel.Public, CertificatePrivilegeLevel.Partner })
            {
                var dir = Path.Combine(certificateStore, CertificateProvisioningService.ProfileName(level));
                if (!CertificateProvisioningService.HasUsableAuthorCert(dir))
                    continue;
                if (!CertificateProvisioningService.GetCoveredDuids(dir).Contains(duid))
                    continue;

                var passwordFile = Path.Combine(dir, Constants.Certificate.PasswordFileName);
                return new SigningProfile(
                    Path.Combine(dir, Constants.Certificate.AuthorFileName),
                    Path.Combine(dir, Constants.Certificate.DistributorFileName),
                    File.ReadAllText(passwordFile).Trim(),
                    dir);
            }

            return null;
        }

        private static (string KeyPem, string ChainPem) Open(string p12Path, string password)
        {
            var collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(p12Path, password, StorageFlags);
            var leaf = collection.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
                ?? throw new InvalidOperationException($"{Path.GetFileName(p12Path)} holds no certificate with a private key.");

            using var rsa = leaf.GetRSAPrivateKey()
                ?? throw new InvalidOperationException($"{Path.GetFileName(p12Path)}: the private key is not RSA.");
            var keyPem = new string(PemEncoding.Write("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()));

            var chain = new StringBuilder();
            foreach (var cert in KeyInfoChain(leaf, collection))
                chain.Append(new string(PemEncoding.Write("CERTIFICATE", cert.RawData))).Append('\n');
            return (keyPem, chain.ToString());
        }

        // The same selection as TizenSdb.Core's signer: the leaf, then the other certificates the
        // .p12 carries that are not self-signed (the root stays out), in the file's order; when the
        // file carries only the leaf, the issuers the OS can chain to, roots again left out.
        private static IReadOnlyList<X509Certificate2> KeyInfoChain(X509Certificate2 leaf, X509Certificate2Collection collection)
        {
            var list = new List<X509Certificate2> { leaf };
            foreach (var cert in collection.Cast<X509Certificate2>())
            {
                if (string.Equals(cert.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsSelfSigned(cert))
                    continue;
                list.Add(cert);
            }

            if (list.Count > 1)
                return list;

            using var chain = new X509Chain
            {
                ChainPolicy = { RevocationMode = X509RevocationMode.NoCheck, VerificationFlags = X509VerificationFlags.NoFlag },
            };
            chain.ChainPolicy.ExtraStore.AddRange(collection);
            if (chain.Build(leaf))
            {
                foreach (var element in chain.ChainElements)
                {
                    var cert = element.Certificate;
                    if (string.Equals(cert.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase) || IsSelfSigned(cert))
                        continue;
                    if (!list.Any(c => string.Equals(c.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                        list.Add(cert);
                }
            }

            return list;
        }

        private static bool IsSelfSigned(X509Certificate2 cert) =>
            string.Equals(cert.Subject, cert.Issuer, StringComparison.OrdinalIgnoreCase);

        // The key has to be exportable, and it should not land in an OS key store on the way: an
        // ephemeral set keeps it in memory, which macOS does not offer (it uses a temporary keychain).
        // iOS refuses the Exportable flag outright (#856/#857), so there the key loads ephemeral
        // only and ExportPkcs8PrivateKey relies on the in-memory key allowing it.
        private static X509KeyStorageFlags StorageFlags =>
            PlatformService.IsAppleMobile
                ? X509KeyStorageFlags.EphemeralKeySet
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? X509KeyStorageFlags.Exportable
                    : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;
    }
}
