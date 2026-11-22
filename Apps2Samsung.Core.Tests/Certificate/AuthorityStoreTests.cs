using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Certificate;

// Which authorities are in use, and when the app has to ask (#1153).
public sealed class AuthorityStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-authorities-").FullName;
    private readonly string _shipped;
    private readonly string _cache;

    public AuthorityStoreTests()
    {
        _shipped = Path.Combine(_dir, "shipped");
        _cache = Path.Combine(_dir, "cache");
        Directory.CreateDirectory(_shipped);
    }

    public void Dispose()
    {
        TizenSdkAssets.DeveloperCaOverrideDirectory = null;
        Directory.Delete(_dir, recursive: true);
    }

    private static byte[] Authority(DateTimeOffset from, DateTimeOffset until)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(from, until);
        return certificate.Export(X509ContentType.Cert);
    }

    private void Ship(DateTimeOffset until)
    {
        foreach (var name in CertificateAuthorities.SamsungFiles)
            File.WriteAllBytes(Path.Combine(_shipped, name), Authority(until.AddYears(-10), until));
    }

    [Fact]
    public void The_shipped_samsung_folder_is_used_until_a_downloaded_set_is_complete()
    {
        var now = DateTimeOffset.UtcNow;
        Ship(now.AddYears(2));
        var store = new AuthorityStore(_cache, _shipped);

        Assert.Equal(_shipped, store.SamsungFolder);

        var downloaded = Path.Combine(_cache, "samsung");
        Directory.CreateDirectory(downloaded);
        File.WriteAllBytes(Path.Combine(downloaded, CertificateAuthorities.SamsungAuthorCa), Authority(now.AddYears(-1), now.AddYears(9)));
        Assert.Equal(_shipped, store.SamsungFolder);

        File.WriteAllBytes(Path.Combine(downloaded, CertificateAuthorities.SamsungPublicCa), Authority(now.AddYears(-1), now.AddYears(9)));
        File.WriteAllBytes(Path.Combine(downloaded, CertificateAuthorities.SamsungPartnerCa), Authority(now.AddYears(-1), now.AddYears(9)));
        Assert.Equal(downloaded, store.SamsungFolder);
    }

    [Fact]
    public void Nothing_has_ended_while_the_shipped_set_runs()
    {
        var now = DateTimeOffset.UtcNow;
        Ship(now.AddYears(2));

        Assert.Null(new AuthorityStore(_cache, _shipped).Ended(AuthoritySet.Samsung, now));
    }

    [Fact]
    public void An_ended_set_is_asked_about_until_the_user_tries_anyway()
    {
        var now = DateTimeOffset.UtcNow;
        Ship(now.AddDays(-3));
        var store = new AuthorityStore(_cache, _shipped);

        var ended = store.Ended(AuthoritySet.Samsung, now);
        Assert.NotNull(ended);
        Assert.True(ended!.EndsAt < now);

        store.AcceptEnded(AuthoritySet.Samsung);
        Assert.Null(store.Ended(AuthoritySet.Samsung, now));
        // The other set is its own question.
        Assert.NotNull(store.Ended(AuthoritySet.Tizen, new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_tizen_authority_ends_on_new_years_day_2027()
    {
        var store = new AuthorityStore(_cache, _shipped);

        Assert.Null(store.Ended(AuthoritySet.Tizen, new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            store.Ended(AuthoritySet.Tizen, new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero))?.EndsAt);
    }

    [Fact]
    public void The_shipped_tizen_pair_is_a_pair_and_a_foreign_certificate_is_not()
    {
        static byte[] All(Stream stream)
        {
            using var memory = new MemoryStream();
            using (stream)
                stream.CopyTo(memory);
            return memory.ToArray();
        }
        var certificate = All(TizenSdkAssets.DeveloperCa());
        var key = All(TizenSdkAssets.DeveloperCaKey());

        Assert.True(TizenLocalCertificateService.IsDeveloperCaPair(certificate, key));

        using var other = RSA.Create(2048);
        var request = new CertificateRequest("CN=Other CA", other, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var foreign = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        Assert.False(TizenLocalCertificateService.IsDeveloperCaPair(System.Text.Encoding.ASCII.GetBytes(foreign.ExportCertificatePem()), key));
        Assert.False(TizenLocalCertificateService.IsDeveloperCaPair(certificate, new byte[] { 1, 2, 3 }));
    }
}
