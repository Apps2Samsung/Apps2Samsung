using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Apps2Samsung.Certificate;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Core.Tests.TvStore;

// What Arm TV-Store writes to the debug log about the hand-over (#1012): the level the TV will read,
// the author fingerprint, the covered TVs, and TV-Store's own answer. Never a key.
public sealed class TvStoreArmLogTests
{
    private static string Pem(X509Certificate2 cert) => new(PemEncoding.Write("CERTIFICATE", cert.RawData));

    private static X509Certificate2 Ca(string cn)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}, OU=VD, O=Samsung Electronics", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    // A distributor certificate as Samsung issues it: signed by the level's CA, the DUIDs in the SAN.
    private static X509Certificate2 Distributor(X509Certificate2 ca, params string[] duids)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=distributor", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var duid in duids)
            san.AddUri(new Uri("URN:tizen:deviceid=" + duid));
        request.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(8);
        return request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
    }

    private static X509Certificate2 Author(DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=author", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
    }

    [Theory]
    [InlineData("VD DEVELOPER Partner CA Class", "Partner")]
    [InlineData("VD DEVELOPER Public CA Class", "Public")]
    public void LevelOf_ReadsSamsungsCaName(string caName, string expected)
    {
        using var ca = Ca(caName);
        using var leaf = Distributor(ca, "KLCFZKUM6IKJE");

        Assert.Equal(expected, TvStoreArmLog.LevelOf(Pem(leaf)));
        // The chain the heads send (leaf + intermediate) reads the same.
        Assert.Equal(expected, TvStoreArmLog.LevelOf(Pem(leaf) + "\n" + Pem(ca)));
    }

    [Fact]
    public void LevelOf_AnyOtherChain_IsUnknownAndSaysHowTheTvTreatsIt()
    {
        using var ca = Ca("Tizen Distributor CA");
        using var leaf = Distributor(ca);

        Assert.StartsWith("unknown", TvStoreArmLog.LevelOf(Pem(leaf)));
        Assert.StartsWith("unknown", TvStoreArmLog.LevelOf("not a certificate"));
    }

    [Fact]
    public void FingerprintOf_IsTheStartOfTheAuthorLeafsSha256()
    {
        using var author = Author(DateTimeOffset.UtcNow.AddYears(1));
        using var other = Author(DateTimeOffset.UtcNow.AddYears(1));
        var expected = Convert.ToHexStringLower(SHA256.HashData(author.RawData))[..12];

        Assert.Equal(expected, TvStoreArmLog.FingerprintOf(Pem(author) + "\n" + Pem(other)));
        Assert.Equal("?", TvStoreArmLog.FingerprintOf(""));
    }

    [Fact]
    public void Describe_NamesLevelAuthorExpiryAndTvs_WithoutKeys()
    {
        using var ca = Ca("VD DEVELOPER Partner CA Class");
        using var distributor = Distributor(ca, "KLCFZKUM6IKJE", "AAAAAAAAAAAAA");
        using var author = Author(new DateTimeOffset(2031, 5, 4, 12, 0, 0, TimeSpan.Zero));
        var pem = new SigningCertificatePem("SECRET-AUTHOR-KEY", Pem(author), "SECRET-DIST-KEY", Pem(distributor) + "\n" + Pem(ca), "Jelly2Sams - Partner");

        var line = TvStoreArmLog.Describe(pem, 1);

        Assert.StartsWith("#2 Jelly2Sams - Partner: Partner, author ", line);
        Assert.Contains(TvStoreArmLog.FingerprintOf(pem.AuthorCertPem), line);
        Assert.Contains("author valid until 2031-05-04", line);
        Assert.Contains("distributor covers AAAAAAAAAAAAA, KLCFZKUM6IKJE", line);
        Assert.DoesNotContain("SECRET", line);
        Assert.DoesNotContain("first choice", line);
        Assert.Contains("(first choice)", TvStoreArmLog.Describe(pem, 0));
    }

    [Fact]
    public void DescribeReply_ListsWhatTheTvHolds()
    {
        var reply = JsonNode.Parse("""
            {"ok":true,"certCount":2,"certificates":[{"name":"Jelly2Sams - Public","level":"public"},{"name":"Jelly2Sams - Partner","level":"partner"}]}
            """);

        Assert.Equal("accepted, 2 certificate(s) on the TV now: Jelly2Sams - Public (public), Jelly2Sams - Partner (partner)",
            TvStoreArmLog.DescribeReply(reply));
    }

    [Fact]
    public void DescribeReply_OlderTvStore_Refusal_AndNoReply()
    {
        Assert.Equal("accepted, 1 certificate(s) on the TV now (this TV-Store does not list them)",
            TvStoreArmLog.DescribeReply(JsonNode.Parse("""{"ok":true,"certCount":1}""")));
        Assert.Equal("refused: need { certificates }",
            TvStoreArmLog.DescribeReply(JsonNode.Parse("""{"ok":false,"error":"need { certificates }"}""")));
        Assert.Equal("no reply", TvStoreArmLog.DescribeReply(null));
    }

    [Fact]
    public void DescribeReply_SaysWhenStoringFailed()
    {
        var reply = JsonNode.Parse("""
            {"ok":true,"certCount":1,"certificates":[{"name":"a","level":"public"}],"certError":"EACCES"}
            """);

        Assert.EndsWith("; storing failed: EACCES", TvStoreArmLog.DescribeReply(reply));
    }
}
