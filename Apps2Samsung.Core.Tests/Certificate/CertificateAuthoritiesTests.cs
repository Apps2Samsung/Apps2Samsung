using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Certificate;

// The shipped certificate authorities end one day (#1153): reading the dates, and taking a newer
// set out of the archives download.tizen.org serves.
public class CertificateAuthoritiesTests
{
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Authority(DateTimeOffset from, DateTimeOffset until, bool pem = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(from, until);
        return pem
            ? Encoding.ASCII.GetBytes("Certificate:\n    Data: text dump first\n" + certificate.ExportCertificatePem())
            : certificate.Export(X509ContentType.Cert);
    }

    private static byte[] Zip(params (string Path, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(content);
            }
        }
        return memory.ToArray();
    }

    [Fact]
    public void The_shipped_authorities_have_the_end_dates_the_issue_names()
    {
        using var stream = TizenSdkAssets.DeveloperCa();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), CertificateAuthorities.EndsAt(memory.ToArray()));
    }

    [Fact]
    public void Ended_lists_what_has_ended_the_first_in_front()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["later.crt"] = Authority(Now.AddYears(-5), Now.AddDays(-1)),
            ["fine.crt"] = Authority(Now.AddYears(-5), Now.AddYears(3)),
            ["first.cer"] = Authority(Now.AddYears(-5), Now.AddMonths(-6), pem: true),
            ["broken.crt"] = new byte[] { 1, 2, 3 },
        };

        var ended = CertificateAuthorities.Ended(files, Now);

        Assert.Equal(new[] { "first.cer", "later.crt" }, ended.Select(e => e.FileName));
    }

    [Fact]
    public void A_replacement_has_to_be_valid_now_and_end_later()
    {
        var current = Authority(Now.AddYears(-10), Now.AddDays(-30));

        Assert.True(CertificateAuthorities.IsUsableReplacement(Authority(Now.AddYears(-1), Now.AddYears(10)), current, Now));
        // The same file again, an already ended one, one that starts in the future, a damaged one.
        Assert.False(CertificateAuthorities.IsUsableReplacement(current, current, Now));
        Assert.False(CertificateAuthorities.IsUsableReplacement(Authority(Now.AddYears(-3), Now.AddDays(-1)), current, Now));
        Assert.False(CertificateAuthorities.IsUsableReplacement(Authority(Now.AddDays(5), Now.AddYears(10)), current, Now));
        Assert.False(CertificateAuthorities.IsUsableReplacement(new byte[] { 9, 9 }, current, Now));
    }

    [Fact]
    public void The_extension_list_names_the_samsung_archive()
    {
        const string xml = """
            <extensionSDK ver="1.0">
              <extension id="x"><name>Something else</name><repository>https://download.tizen.org/sdk/extensions/other.zip</repository></extension>
              <extension id="760ffffa"><name>Samsung Certificate Extension</name>
                <repository>
                  https://download.tizen.org/sdk/extensions/tizen-certificate-extension_2.0.75.zip
                </repository></extension>
            </extensionSDK>
            """;

        Assert.Equal("https://download.tizen.org/sdk/extensions/tizen-certificate-extension_2.0.75.zip", CertificateAuthorities.SamsungExtensionUrl(xml));
    }

    [Theory]
    [InlineData("<extensionSDK><extension><name>Samsung Certificate Extension</name><repository>https://example.org/x.zip</repository></extension></extensionSDK>")]
    [InlineData("<extensionSDK><extension><name>Samsung Certificate Extension</name><repository>http://download.tizen.org/x.zip</repository></extension></extensionSDK>")]
    [InlineData("<extensionSDK></extensionSDK>")]
    [InlineData("not xml at all")]
    public void An_archive_on_another_host_or_none_is_no_archive(string xml)
    {
        Assert.Null(CertificateAuthorities.SamsungExtensionUrl(xml));
    }

    [Fact]
    public void The_package_list_names_the_generator()
    {
        const string list = "Package : Certificate-Manager\nVersion : 3.1.3\nPath : /binary/Certificate-Manager_3.1.3_ubuntu-64.zip\n\n" +
                            "Package : certificate-generator\nVersion : 0.1.4\nOS : ubuntu-64\nPath : /binary/certificate-generator_0.1.4_ubuntu-64.zip\nSize : 4319612\n\n" +
                            "Package : certificate-encryptor\nPath : /binary/certificate-encryptor_1.0.10_ubuntu-64.zip\n";

        Assert.Equal("https://download.tizen.org/sdk/tizenstudio/official/binary/certificate-generator_0.1.4_ubuntu-64.zip", CertificateAuthorities.TizenGeneratorUrl(list));
    }

    [Theory]
    [InlineData("Package : certificate-generator\nPath : /binary/../../elsewhere.zip\n")]
    [InlineData("Package : certificate-generator\nPath : https://example.org/x.zip\n")]
    [InlineData("Package : something\nPath : /binary/something.zip\n")]
    public void A_path_that_leaves_the_package_folder_is_no_package(string list)
    {
        Assert.Null(CertificateAuthorities.TizenGeneratorUrl(list));
    }

    [Fact]
    public void The_samsung_authorities_come_out_of_the_three_nested_archives()
    {
        var author = Authority(Now.AddYears(-1), Now.AddYears(10));
        var jar = Zip(
            ("res/ca/vd_tizen_dev_author_ca.cer", author),
            ("res/ca/vd_tizen_dev_public2.crt", new byte[] { 2 }),
            ("res/ca/vd_tizen_dev_partner2.crt", new byte[] { 3 }),
            ("res/ca/vd_tizen_dev_platform2.crt", new byte[] { 4 }));
        var addOn = Zip(
            ("data/ide/plugins/org.tizen.common.cert_1.0.0.202510211036.jar", new byte[] { 0 }),
            ("data/tools/certificate-manager/plugins/org.tizen.common.cert_1.0.0.202510211036.jar", jar));
        var extension = Zip(("pkg_list_ubuntu-64", new byte[] { 0 }), ("binary/cert-add-on_2.0.75_ubuntu-64.zip", addOn));

        var download = CertificateAuthorities.ExtractSamsung(new MemoryStream(extension));

        Assert.NotNull(download);
        Assert.Equal(AuthoritySet.Samsung, download!.Set);
        Assert.Equal(3, download.Files.Count);
        Assert.Equal(author, download.Files[CertificateAuthorities.SamsungAuthorCa]);
    }

    [Fact]
    public void A_samsung_archive_missing_one_of_the_three_is_nothing()
    {
        var jar = Zip(("res/ca/vd_tizen_dev_author_ca.cer", new byte[] { 1 }), ("res/ca/vd_tizen_dev_public2.crt", new byte[] { 2 }));
        var addOn = Zip(("data/tools/certificate-manager/plugins/org.tizen.common.cert_1.jar", jar));
        var extension = Zip(("binary/cert-add-on_2.0.75_ubuntu-64.zip", addOn));

        Assert.Null(CertificateAuthorities.ExtractSamsung(new MemoryStream(extension)));
    }

    [Fact]
    public void The_tizen_authority_comes_with_its_key()
    {
        var package = Zip(
            ("data/tools/certificate-generator/certificates/developer/tizen-developer-ca.cer", new byte[] { 7 }),
            ("data/tools/certificate-generator/certificates/developer/tizen-developer-ca-privatekey.pem", new byte[] { 8 }),
            ("data/tools/certificate-generator/certificates/distributor/tizen-distributor-ca.cer", new byte[] { 9 }));

        var download = CertificateAuthorities.ExtractTizen(new MemoryStream(package));

        Assert.NotNull(download);
        Assert.Equal(new byte[] { 7 }, download!.Files[CertificateAuthorities.TizenDeveloperCa]);
        Assert.Equal(new byte[] { 8 }, download.Files[CertificateAuthorities.TizenDeveloperCaKey]);
    }

    [Fact]
    public void A_generator_package_without_the_key_is_nothing()
    {
        var package = Zip(("certificate-generator/certificates/developer/tizen-developer-ca.cer", new byte[] { 7 }));

        Assert.Null(CertificateAuthorities.ExtractTizen(new MemoryStream(package)));
    }
}
