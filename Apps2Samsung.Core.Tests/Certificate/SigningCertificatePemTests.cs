using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.Certificate;

// Arm TV-Store hands over every signing profile a device holds (#44): which folders count as a
// profile, how the list is opened and deduplicated, and the JSON the TV-Store page takes.
public sealed class SigningCertificatePemTests : IDisposable
{
    private readonly string _store = Directory.CreateTempSubdirectory("a2s-certs-").FullName;

    public void Dispose() => Directory.Delete(_store, recursive: true);

    private static X509Certificate2 SelfSigned(string cn)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    // A profile folder as the heads write them: author.p12, distributor.p12, password.txt.
    private string Profile(string name, X509Certificate2 author, X509Certificate2 distributor, string password = "pw")
    {
        var dir = Path.Combine(_store, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "author.p12"), author.Export(X509ContentType.Pkcs12, password));
        File.WriteAllBytes(Path.Combine(dir, "distributor.p12"), distributor.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(Path.Combine(dir, "password.txt"), password + "\n");
        return dir;
    }

    private static SigningProfile ProfileOf(string dir) =>
        new(Path.Combine(dir, "author.p12"), Path.Combine(dir, "distributor.p12"), File.ReadAllText(Path.Combine(dir, "password.txt")).Trim(), dir);

    [Fact]
    public void ProfilesIn_ListsCompleteProfiles_AndSkipsTheRest()
    {
        using var author = SelfSigned("author");
        using var distributor = SelfSigned("distributor");
        Profile("Jelly2Sams - Public", author, distributor);
        Profile("Jellyfin", author, distributor);
        var noDistributor = Profile("Half", author, distributor);
        File.Delete(Path.Combine(noDistributor, "distributor.p12"));
        Directory.CreateDirectory(Path.Combine(_store, "Empty"));

        var names = SigningCertificatePem.ProfilesIn(_store, Path.Combine(_store, "does-not-exist"), null)
            .Select(p => Path.GetFileName(p.ProfileDir)).ToList();

        Assert.Equal(new[] { "Jelly2Sams - Public", "Jellyfin" }, names);
    }

    [Fact]
    public void OpenAll_PutsThePrimaryFirst_DropsRepeats_SkipsWhatWillNotOpen()
    {
        using var authorA = SelfSigned("author A");
        using var authorB = SelfSigned("author B");
        using var distributor = SelfSigned("distributor");
        var publicDir = Profile("Jelly2Sams - Public", authorA, distributor);
        Profile("Jelly2Sams - Partner", authorA, distributor);      // same author: one entry is enough
        Profile("Jellyfin", authorB, distributor);
        var broken = Profile("Broken", authorB, distributor);
        File.WriteAllText(Path.Combine(broken, "password.txt"), "wrong");

        var list = SigningCertificatePem.OpenAll(ProfileOf(publicDir), SigningCertificatePem.ProfilesIn(_store));

        Assert.Equal(new[] { "Jelly2Sams - Public", "Jellyfin" }, list.Select(c => c.Name));
        Assert.All(list, c => Assert.Contains("PRIVATE KEY", c.AuthorKeyPem));
        Assert.All(list, c => Assert.Contains("BEGIN CERTIFICATE", c.DistributorCertPem));
    }

    [Fact]
    public void ArmPayload_KeepsTheOldShape_AndAddsEveryProfile()
    {
        var first = new SigningCertificatePem("ak1", "ac1", "dk1", "dc1", "Jelly2Sams - Public");
        var second = new SigningCertificatePem("ak2", "ac2", "dk2", "dc2");

        using var doc = JsonDocument.Parse(TvStoreClient.ArmPayload(new[] { first, second }));
        var root = doc.RootElement;

        Assert.Equal("ak1", root.GetProperty("authorKeyPem").GetString());
        Assert.Equal("dc1", root.GetProperty("distributorCertPem").GetString());
        var all = root.GetProperty("certificates").EnumerateArray().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal("Jelly2Sams - Public", all[0].GetProperty("name").GetString());
        Assert.Equal("certificate 2", all[1].GetProperty("name").GetString());
        Assert.Equal("ac2", all[1].GetProperty("authorCertPem").GetString());
        Assert.Throws<ArgumentException>(() => TvStoreClient.ArmPayload(Array.Empty<SigningCertificatePem>()));
    }
}
