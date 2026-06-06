using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Tizen.Certificate;
using Apps2Samsung.Interfaces;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class TizenCertificateService : ITizenCertificateService
    {
        private readonly HttpClient _httpClient;
        private readonly IDialogService _dialogService;

        public TizenCertificateService(
            HttpClient httpClient,
            IDialogService dialogService)
        {
            _httpClient = httpClient;
            _dialogService = dialogService;
        }

        public async Task<(string authorP12, string distributorP12, string passwordP12)> GenerateProfileAsync(
            string duid,
            string accessToken,
            string userId,
            string userEmail,
            string outputPath,
            ProgressCallback? progress = null)
        {
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentException("Output path cannot be empty", nameof(outputPath));

            var cipherUtil = new CipherUtil();

            Directory.CreateDirectory(outputPath);

            progress?.Invoke("GenPassword".Localized());
            string p12Plain = cipherUtil.GenerateRandomPassword();
            string p12Encrypted = cipherUtil.GetEncryptedString(p12Plain);
            await File.WriteAllTextAsync(Path.Combine(outputPath, "password.txt"), p12Plain);

            progress?.Invoke("GenKeyPair".Localized());
            var keyPair = GenerateKeyPair();

            progress?.Invoke("CreateAuthorCsr".Localized());
            var authorCsrData = GenerateAuthorCsr(keyPair);
            await File.WriteAllBytesAsync(Path.Combine(outputPath, "author.csr"), authorCsrData);

            progress?.Invoke("CreateDistributorCSR".Localized());
            var distributorCsrData = GenerateDistributorCsr(keyPair, duid, userEmail);
            await File.WriteAllBytesAsync(Path.Combine(outputPath, "distributor.csr"), distributorCsrData);

            progress?.Invoke("PostAuthorCSR".Localized());
            var signedAuthorCsrBytes = await PostAuthorCsrAsync(authorCsrData, accessToken, userId);
            await File.WriteAllBytesAsync(Path.Combine(outputPath, "signed_author.cer"), signedAuthorCsrBytes);

            progress?.Invoke("PostDistributorCSR".Localized());
            var (profileXmlBytes, signedDistributorCsrBytes) = await PostDistributorCsrAsync(accessToken, userId, distributorCsrData, duid);
            if (profileXmlBytes != null)
                await File.WriteAllBytesAsync(Path.Combine(outputPath, "device-profile.xml"), profileXmlBytes);
            await File.WriteAllBytesAsync(Path.Combine(outputPath, "signed_distributor.cer"), signedDistributorCsrBytes);

            await CheckCertificateExistenceAsync(Path.Combine(AppSettings.ProfilePath, "ca"));

            progress?.Invoke("ExportPfxCertificates".Localized());
            string authorp12 = await ExportPfxWithCaChainAsync(signedAuthorCsrBytes, keyPair.Private, p12Plain, outputPath, Path.Combine(AppSettings.ProfilePath, "ca"), "author", "vd_tizen_dev_author_ca.cer");
            string distributorp12 = await ExportPfxWithCaChainAsync(signedDistributorCsrBytes, keyPair.Private, p12Plain, outputPath, Path.Combine(AppSettings.ProfilePath, "ca"), "distributor", "vd_tizen_dev_public2.crt");
            return (authorp12, distributorp12, p12Plain);
        }

        private static AsymmetricCipherKeyPair GenerateKeyPair()
        {
            var keyGen = new RsaKeyPairGenerator();
            keyGen.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
            return keyGen.GenerateKeyPair();
        }

        private static byte[] GenerateAuthorCsr(AsymmetricCipherKeyPair keyPair)
        {
            var subject = new X509Name(
                new ArrayList { X509Name.C, X509Name.ST, X509Name.L, X509Name.O, X509Name.OU, X509Name.CN },
                new ArrayList { "", "", "", "", "", "Jelly2Sams" });
            var csr = new Pkcs10CertificationRequest("SHA256withRSA", subject, keyPair.Public, null, keyPair.Private);

            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
            var pemWriter = new PemWriter(sw);
            pemWriter.WriteObject(csr);
            sw.Flush();
            ms.Position = 0;
            return ms.ToArray();

        }

        private static byte[] GenerateDistributorCsr(AsymmetricCipherKeyPair keyPair, string duid, string userEmail)
        {
            var subject = new X509Name(
                new ArrayList { X509Name.CN, X509Name.OU, X509Name.O, X509Name.L, X509Name.ST, X509Name.C, X509Name.EmailAddress },
                new ArrayList { "TizenSDK", "", "", "", "", "", userEmail });
            var generalNames = new GeneralNames(new[]
            {
                new GeneralName(GeneralName.UniformResourceIdentifier, "URN:tizen:packageid="),
                new GeneralName(GeneralName.UniformResourceIdentifier, $"URN:tizen:deviceid={duid}")
            });

            var extensions = new X509Extensions(new Dictionary<DerObjectIdentifier, Org.BouncyCastle.Asn1.X509.X509Extension>
            {
                { X509Extensions.SubjectAlternativeName, new Org.BouncyCastle.Asn1.X509.X509Extension(false, new DerOctetString(generalNames)) }
            });

            var attribute = new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest, new DerSet(extensions));
            var csr = new Pkcs10CertificationRequest("SHA256withRSA", subject, keyPair.Public, new DerSet(attribute), keyPair.Private);

            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
            var pemWriter = new PemWriter(sw);
            pemWriter.WriteObject(csr);
            sw.Flush();
            ms.Position = 0;
            return ms.ToArray();

        }

        private async Task CheckCertificateExistenceAsync(string caPath)
        {
            string[] requiredFiles = { "vd_tizen_dev_author_ca.cer", "vd_tizen_dev_public2.crt" };

            foreach (var file in requiredFiles)
            {
                string target = Path.Combine(caPath, file);
                if (!File.Exists(target))
                    await _dialogService.ShowErrorAsync($"Missing CA file: {file}");
            }
        }

        // Simplified Http Post for Author CSR
        private async Task<byte[]> PostAuthorCsrAsync(byte[] csrData, string accessToken, string userId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, AppSettings.Default.AuthorEndpoint_V3);
            var content = new MultipartFormDataContent
            {
                { new StringContent(accessToken), "access_token" },
                { new StringContent(userId), "user_id" },
                { new StringContent("VD"), "platform"},
                { new ByteArrayContent(csrData), "csr", "author.csr" }
            };
            request.Content = content;
            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        private async Task<(byte[] profileXml, byte[] distributorCert)> PostDistributorCsrAsync(string accessToken, string userId, byte[] csrBytes, string duid)
        {
            var certificateHelper = new CertificateHelper();

            var v1Content = new MultipartFormDataContent
            {
                { new StringContent(accessToken), "access_token" },
                { new StringContent(userId), "user_id" },
                { new StringContent("Public"), "privilege_level" },
                { new StringContent("Individual"), "developer_type" },
                { new StringContent("VD"), "platform" },
                { new ByteArrayContent(csrBytes), "csr", "distributor.csr" }
            };

            var v1Request = new HttpRequestMessage(HttpMethod.Post, AppSettings.Default.DistributorsEndpoint_V1);
            v1Request.Content = v1Content;
            var v1Response = await _httpClient.SendAsync(v1Request);

            if (!v1Response.IsSuccessStatusCode)
            {
                await certificateHelper.HandleErrorResponse(v1Response);
                v1Response.EnsureSuccessStatusCode();
            }

            var profileXml = await v1Response.Content.ReadAsByteArrayAsync();

            var v3Content = new MultipartFormDataContent
            {
                { new StringContent(accessToken), "access_token" },
                { new StringContent(userId), "user_id" },
                { new StringContent("Public"), "privilege_level" },
                { new StringContent("Individual"), "developer_type" },
                { new StringContent("VD"), "platform" },
                { new ByteArrayContent(csrBytes), "csr", "distributor.csr" }
            };

            var v3Request = new HttpRequestMessage(HttpMethod.Post, AppSettings.Default.DistributorsEndpoint_V3);
            v3Request.Content = v3Content;
            var v3Response = await _httpClient.SendAsync(v3Request);

            if (!v3Response.IsSuccessStatusCode)
            {
                await certificateHelper.HandleErrorResponse(v3Response);
                v3Response.EnsureSuccessStatusCode();
            }

            var distributorCert = await v3Response.Content.ReadAsByteArrayAsync();

            return (profileXml, distributorCert);
        }


        private static async Task<string> ExportPfxWithCaChainAsync(
            byte[] signedCertBytes,
            AsymmetricKeyParameter privateKey,
            string password,
            string outputPath,
            string caPath,
            string filename,
            string caFile /* kept for compat but verified below */)
        {
            var parser = new Org.BouncyCastle.X509.X509CertificateParser();

            var leafBc = parser.ReadCertificate(signedCertBytes);
            using var leafDot = new X509Certificate2(leafBc.GetEncoded());

            X509Certificate2? candidateDot = null;
            string requested = Path.Combine(caPath, caFile);
            if (File.Exists(requested))
            {
                var bc = parser.ReadCertificate(await File.ReadAllBytesAsync(requested));
                candidateDot = new X509Certificate2(bc.GetEncoded());
            }

            // --- Load all CAs in folder as fallback pool ---
            var allCaDot = new List<X509Certificate2>();
            if (Directory.Exists(caPath))
            {
                foreach (var p in Directory.EnumerateFiles(caPath, "*.*")
                         .Where(f => f.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".crt", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var caTemp = parser.ReadCertificate(await File.ReadAllBytesAsync(p));
                        allCaDot.Add(new X509Certificate2(caTemp.GetEncoded()));
                    }
                    catch { /* ignore bad files */ }
                }
            }

            static bool IsMatchingIntermediate(X509Certificate2 ca, X509Certificate2 ee) =>
                !ca.Subject.Equals(ca.Issuer, StringComparison.Ordinal) &&  // not self-signed
                 ca.Subject.Equals(ee.Issuer, StringComparison.Ordinal);    // issuer match

            // --- Pick/verify the correct INTERMEDIATE (never a root) ---
            if (candidateDot == null || !IsMatchingIntermediate(candidateDot, leafDot))
            {
                candidateDot?.Dispose();
                candidateDot = allCaDot.FirstOrDefault(c => IsMatchingIntermediate(c, leafDot))
                    ?? throw new InvalidOperationException(
                        $"No matching intermediate in '{caPath}'. Expected Subject: '{leafDot.Issuer}'.");
            }

            // --- Convert chosen CA back to BC type for Pkcs12Store ---
            var caBcCert = parser.ReadCertificate(candidateDot.RawData);

            // --- Build PKCS#12 deterministically with BC (alias + order) ---
            var store = new Pkcs12StoreBuilder().Build();

            var keyEntry = new AsymmetricKeyEntry(privateKey);
            var leafEntry = new X509CertificateEntry(leafBc);
            var caEntry = new X509CertificateEntry(caBcCert);

            // Chain MUST be [leaf, intermediate]
            var chain = new[] { leafEntry, caEntry };

            const string keyAlias = "usercertificate"; // visible in cert viewers
            store.SetKeyEntry(keyAlias, keyEntry, chain);
            string caAlias = caBcCert.SubjectDN.ToString();  // keeps escaped commas (\,) as expected
            store.SetCertificateEntry(caAlias, caEntry);

            // --- Write .p12 ---
            var target = Path.Combine(outputPath, $"{filename}.p12");
            using (var ms = new MemoryStream())
            {
                store.Save(ms, password.ToCharArray(), new SecureRandom());
                await File.WriteAllBytesAsync(target, ms.ToArray());
            }

            // --- Optional sanity check ---
            if (PlatformService.IsWindows)
            {
                try
                {
                    var verify = new X509Certificate2Collection();
                    // For a one-off verification, EphemeralKeySet is fine
                    verify.Import(target, password, X509KeyStorageFlags.EphemeralKeySet);

                    var leaf = verify.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
                               ?? throw new InvalidOperationException("PFX sanity failed: no leaf with private key.");
                    var ca = verify.Cast<X509Certificate2>().FirstOrDefault(c => !c.HasPrivateKey)
                               ?? throw new InvalidOperationException("PFX sanity failed: no intermediate certificate.");

                    if (!string.Equals(leaf.Issuer, ca.Subject, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"PFX chain mismatch: leaf issuer '{leaf.Issuer}' != CA subject '{ca.Subject}'.");
                }
                catch (Exception ex)
                {
                    // Log if you want, but don't block the install on Linux quirks
                    System.Diagnostics.Trace.WriteLine($"PFX sanity check failed: {ex}");
                }
            }

            return target;
        }
        private static X509KeyStorageFlags GetX509KeyStorageFlags()
        {
            return PlatformService.GetX509KeyStorageFlags();
        }
    }
}