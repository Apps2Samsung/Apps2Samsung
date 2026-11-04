using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace Apps2Samsung.Certificate
{
    /// <summary>
    /// Makes a Tizen certificate profile (#988): what Tizen Studio's Certificate Manager and the VS
    /// Code Tizen extension call a "Tizen certificate", as opposed to the Samsung one
    /// (<see cref="Services.TizenCertificateService"/>). Nothing here talks to Samsung and no TV is
    /// involved: the author certificate is signed locally by the SDK's Tizen Developers CA, and the
    /// distributor is the SDK's ready-made signer for the level, which is the same on every machine.
    /// Whether a TV accepts the result is the TV's decision (a Tizen 5.0 set takes the legacy Public
    /// signer and refuses the rest, see the issue); the Samsung flow is untouched.
    ///
    /// The profile has the same three files as a Samsung one (<c>author.p12</c>, <c>distributor.p12</c>,
    /// <c>password.txt</c>, one password for both), so the signer, the backup and the certificate
    /// archive take it as they are. The SDK distributor p12 is re-exported under the profile's password
    /// because the signing engine opens both files with one password.
    /// </summary>
    public static class TizenLocalCertificateService
    {
        /// <summary>The author certificate's subject when the caller names none.</summary>
        public const string DefaultCommonName = "Apps2Samsung";

        public const string AuthorFileName = "author.p12";
        public const string DistributorFileName = "distributor.p12";
        public const string PasswordFileName = "password.txt";

        /// <summary>
        /// Writes a new profile into <paramref name="profileDir"/> (made when missing; an existing
        /// profile there is replaced). <paramref name="progress"/> gets en.json keys, as the Samsung
        /// service reports them.
        /// </summary>
        public static async Task<TizenCertificateProfile> CreateProfileAsync(
            string profileDir, TizenDistributorLevel level, TizenDistributorChain? chain = null, string? commonName = null, ProgressCallback? progress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profileDir);
            var signerChain = chain ?? TizenSdkAssets.DefaultChain(level);
            if (!TizenSdkAssets.HasDistributorSigner(level, signerChain))
                throw new ArgumentException($"The Tizen SDK has no {signerChain} distributor signer for {level}.", nameof(chain));
            Directory.CreateDirectory(profileDir);

            progress?.Invoke("GenPassword");
            var password = new CipherUtil().GenerateRandomPassword();

            progress?.Invoke("GenKeyPair");
            var keyPair = GenerateKeyPair();

            progress?.Invoke("SignTizenAuthor");
            var (caCert, caKey) = LoadDeveloperCa();
            var author = BuildAuthorCertificate(keyPair.Public, caCert, caKey, string.IsNullOrWhiteSpace(commonName) ? DefaultCommonName : commonName.Trim());

            progress?.Invoke("ExportPfxCertificates");
            var authorP12 = Path.Combine(profileDir, AuthorFileName);
            var distributorP12 = Path.Combine(profileDir, DistributorFileName);
            var passwordFile = Path.Combine(profileDir, PasswordFileName);
            await File.WriteAllBytesAsync(authorP12, BuildP12(keyPair.Private, new[] { author, caCert }, password));
            await File.WriteAllBytesAsync(distributorP12, RepackDistributor(level, signerChain, password));
            await File.WriteAllTextAsync(passwordFile, password);

            return new TizenCertificateProfile(authorP12, distributorP12, password, profileDir, Path.GetFileName(profileDir), level, signerChain);
        }

        private static AsymmetricCipherKeyPair GenerateKeyPair()
        {
            var generator = new RsaKeyPairGenerator();
            generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            return generator.GenerateKeyPair();
        }

        /// <summary>The SDK's Tizen Developers CA certificate and private key, from the embedded files.</summary>
        internal static (X509Certificate Certificate, AsymmetricKeyParameter PrivateKey) LoadDeveloperCa()
        {
            X509Certificate cert;
            using (var reader = new StreamReader(TizenSdkAssets.DeveloperCa()))
            {
                // The file is openssl's text dump followed by the PEM block; the reader skips to BEGIN.
                cert = new PemReader(reader).ReadObject() as X509Certificate
                    ?? throw new InvalidOperationException("The embedded Tizen Developers CA certificate could not be read.");
            }

            AsymmetricKeyParameter key;
            using (var reader = new StreamReader(TizenSdkAssets.DeveloperCaKey()))
            {
                var read = new PemReader(reader, new FixedPassword(TizenSdkAssets.DeveloperCaKeyPassword)).ReadObject();
                key = read switch
                {
                    AsymmetricCipherKeyPair pair => pair.Private,
                    AsymmetricKeyParameter { IsPrivate: true } single => single,
                    _ => throw new InvalidOperationException("The embedded Tizen Developers CA private key could not be read."),
                };
            }
            return (cert, key);
        }

        /// <summary>
        /// The author certificate as the VS Code extension's <c>generateTizenCert</c> makes it: issued
        /// by the Tizen Developers CA, one year, subject CN only, not a CA, digitalSignature +
        /// keyEncipherment, clientAuth + emailProtection, a subject key identifier, SHA-256.
        /// </summary>
        internal static X509Certificate BuildAuthorCertificate(
            AsymmetricKeyParameter publicKey, X509Certificate caCert, AsymmetricKeyParameter caKey, string commonName)
        {
            var now = DateTime.UtcNow;
            var generator = new X509V3CertificateGenerator();
            // The extension uses the millisecond clock as the serial; same here, so two profiles never share one.
            generator.SetSerialNumber(new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
            generator.SetIssuerDN(caCert.SubjectDN);
            generator.SetSubjectDN(new X509Name("CN=" + commonName.Replace(",", "\\,")));
            generator.SetNotBefore(now);
            generator.SetNotAfter(now.AddYears(1));
            generator.SetPublicKey(publicKey);
            generator.AddExtension(X509Extensions.BasicConstraints, false, new BasicConstraints(false));
            generator.AddExtension(X509Extensions.KeyUsage, false, new KeyUsage(KeyUsage.DigitalSignature | KeyUsage.KeyEncipherment));
            generator.AddExtension(X509Extensions.ExtendedKeyUsage, false, new ExtendedKeyUsage(KeyPurposeID.IdKPClientAuth, KeyPurposeID.IdKPEmailProtection));
            generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false, new SubjectKeyIdentifierStructure(publicKey));
            return generator.Generate(new Asn1SignatureFactory("SHA256withRSA", caKey, new SecureRandom()));
        }

        /// <summary>
        /// A PKCS#12 with the key under the leaf (<paramref name="chain"/>[0]) and every certificate of
        /// the chain, 3DES for the key and the certificate bags: the cipher the Samsung profiles use,
        /// as Android, iOS and OpenSSL 3 hosts refuse the legacy RC2 default.
        /// </summary>
        private static byte[] BuildP12(AsymmetricKeyParameter privateKey, IReadOnlyList<X509Certificate> chain, string password)
        {
            var store = new Pkcs12StoreBuilder()
                .SetKeyAlgorithm(PkcsObjectIdentifiers.PbeWithShaAnd3KeyTripleDesCbc)
                .SetCertAlgorithm(PkcsObjectIdentifiers.PbeWithShaAnd3KeyTripleDesCbc)
                .Build();
            var entries = chain.Select(c => new X509CertificateEntry(c)).ToArray();
            store.SetKeyEntry("usercertificate", new AsymmetricKeyEntry(privateKey), entries);
            foreach (var entry in entries.Skip(1))
                store.SetCertificateEntry(entry.Certificate.SubjectDN.ToString(), entry);

            using var ms = new MemoryStream();
            store.Save(ms, password.ToCharArray(), new SecureRandom());
            return ms.ToArray();
        }

        /// <summary>The SDK distributor signer for <paramref name="level"/> and <paramref name="chain"/>, re-exported under <paramref name="password"/>.</summary>
        internal static byte[] RepackDistributor(TizenDistributorLevel level, TizenDistributorChain chain, string password)
        {
            var sdk = new Pkcs12StoreBuilder().Build();
            using (var stream = TizenSdkAssets.DistributorSigner(level, chain))
                sdk.Load(stream, TizenSdkAssets.DistributorSignerPassword.ToCharArray());

            var alias = sdk.Aliases.Cast<string>().FirstOrDefault(sdk.IsKeyEntry)
                ?? throw new InvalidOperationException($"The SDK {level} distributor signer has no private key entry.");
            var certs = sdk.GetCertificateChain(alias).Select(e => e.Certificate).ToList();
            return BuildP12(sdk.GetKey(alias).Key, certs, password);
        }

        private sealed class FixedPassword : IPasswordFinder
        {
            private readonly char[] _password;
            public FixedPassword(string password) => _password = password.ToCharArray();
            public char[] GetPassword() => (char[])_password.Clone();
        }
    }
}
