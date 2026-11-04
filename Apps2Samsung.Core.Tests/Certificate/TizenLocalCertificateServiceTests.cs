using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Certificate;

/// <summary>
/// The Tizen certificate profile (#988): made from the embedded SDK files alone, the same three files
/// as a Samsung profile, the author signed by the Tizen Developers CA and the distributor the SDK's
/// signer for the level, both opening with the one password in password.txt.
/// </summary>
public sealed class TizenLocalCertificateServiceTests : IDisposable
{
    private readonly string _store = Directory.CreateTempSubdirectory("a2s-tizen-").FullName;

    public void Dispose() => Directory.Delete(_store, recursive: true);

    private static X509Certificate2Collection Open(string p12, string password)
    {
        var collection = new X509Certificate2Collection();
        collection.Import(p12, password, X509KeyStorageFlags.EphemeralKeySet);
        return collection;
    }

    [Theory]
    [InlineData(TizenDistributorLevel.Public, null, "Tizen Public Distributor Signer", "Tizen Public Distributor CA")]
    [InlineData(TizenDistributorLevel.Partner, null, "Tizen Partner Distributor Signer", "Tizen Partner Distributor CA")]
    [InlineData(TizenDistributorLevel.Platform, null, "Tizen Studio Platform Signer", "Tizen Studio Platform CA Class")]
    [InlineData(TizenDistributorLevel.Public, TizenDistributorChain.New, "Tizen Studio Public Signer", "Tizen Studio Public CA Class")]
    [InlineData(TizenDistributorLevel.Partner, TizenDistributorChain.New, "Tizen Studio Partner Signer", "Tizen Studio Partner CA Class")]
    [InlineData(TizenDistributorLevel.Public, TizenDistributorChain.Legacy, "Tizen Public Distributor Signer", "Tizen Public Distributor CA")]
    public async Task A_profile_has_the_author_from_the_developer_ca_and_the_sdk_distributor_for_the_level(
        TizenDistributorLevel level, TizenDistributorChain? chain, string distributorSigner, string distributorCa)
    {
        var profile = await TizenLocalCertificateService.CreateProfileAsync(Path.Combine(_store, "p"), level, chain);

        Assert.Equal(Path.Combine(_store, "p", "author.p12"), profile.AuthorP12);
        Assert.Equal(Path.Combine(_store, "p", "distributor.p12"), profile.DistributorP12);
        Assert.Equal(profile.Password, File.ReadAllText(Path.Combine(_store, "p", "password.txt")));
        Assert.Equal(level, profile.Level);

        var author = Open(profile.AuthorP12, profile.Password);
        var leaf = Assert.Single(author.Cast<X509Certificate2>(), c => c.HasPrivateKey);
        Assert.Equal("CN=Apps2Samsung", leaf.Subject);
        Assert.Contains("Tizen Developers CA", leaf.Issuer);
        Assert.Contains(author.Cast<X509Certificate2>(), c => !c.HasPrivateKey && c.Subject.Contains("Tizen Developers CA"));
        Assert.InRange(leaf.NotAfter.ToUniversalTime(), DateTime.UtcNow.AddDays(364), DateTime.UtcNow.AddDays(367));
        Assert.True(leaf.NotBefore.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1));

        var distributor = Open(profile.DistributorP12, profile.Password);
        var signer = Assert.Single(distributor.Cast<X509Certificate2>(), c => c.HasPrivateKey);
        Assert.Contains(distributorSigner, signer.Subject);
        Assert.Contains(distributor.Cast<X509Certificate2>(), c => !c.HasPrivateKey && c.Subject.Contains(distributorCa));
        Assert.Equal(chain ?? TizenSdkAssets.DefaultChain(level), profile.Chain);
    }

    [Fact]
    public async Task Platform_has_no_legacy_signer()
    {
        Assert.False(TizenSdkAssets.HasDistributorSigner(TizenDistributorLevel.Platform, TizenDistributorChain.Legacy));
        Assert.Equal(TizenDistributorChain.New, TizenSdkAssets.DefaultChain(TizenDistributorLevel.Platform));
        Assert.Equal(TizenDistributorChain.Legacy, TizenSdkAssets.DefaultChain(TizenDistributorLevel.Public));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TizenLocalCertificateService.CreateProfileAsync(Path.Combine(_store, "p"), TizenDistributorLevel.Platform, TizenDistributorChain.Legacy));
    }

    [Fact]
    public async Task The_author_certificate_has_the_extensions_the_sdk_sets()
    {
        var profile = await TizenLocalCertificateService.CreateProfileAsync(Path.Combine(_store, "p"), TizenDistributorLevel.Public, commonName: "patrick");
        var leaf = Open(profile.AuthorP12, profile.Password).Cast<X509Certificate2>().Single(c => c.HasPrivateKey);

        Assert.Equal("CN=patrick", leaf.Subject);
        var basic = Assert.IsType<X509BasicConstraintsExtension>(leaf.Extensions["2.5.29.19"]);
        Assert.False(basic.CertificateAuthority);
        var usage = Assert.IsType<X509KeyUsageExtension>(leaf.Extensions["2.5.29.15"]);
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.True(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment));
        var eku = Assert.IsType<X509EnhancedKeyUsageExtension>(leaf.Extensions["2.5.29.37"]);
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.4");
        Assert.NotNull(leaf.Extensions["2.5.29.14"]);
    }

    [Fact]
    public async Task The_store_reuses_a_profile_and_makes_one_per_level()
    {
        Assert.False(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Public));

        var first = await TizenProfileStore.EnsureAsync(_store, TizenDistributorLevel.Public);
        Assert.Equal("Tizen - Public", first.ProfileName);
        Assert.Equal(Path.Combine(_store, "Tizen - Public"), first.ProfileDir);
        Assert.True(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Public));
        Assert.False(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Partner));

        var again = await TizenProfileStore.EnsureAsync(_store, TizenDistributorLevel.Public);
        Assert.Equal(first.Password, again.Password);
        Assert.Equal(File.GetLastWriteTimeUtc(first.AuthorP12), File.GetLastWriteTimeUtc(again.AuthorP12));

        var forced = await TizenProfileStore.EnsureAsync(_store, TizenDistributorLevel.Public, force: true);
        Assert.NotEqual(first.Password, forced.Password);

        var partner = await TizenProfileStore.EnsureAsync(_store, TizenDistributorLevel.Partner);
        Assert.Equal("Tizen - Partner", partner.ProfileName);
        Assert.True(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Partner));

        // The other signer generation is a profile of its own, next to the default one.
        var publicNew = await TizenProfileStore.EnsureAsync(_store, TizenDistributorLevel.Public, TizenDistributorChain.New);
        Assert.Equal("Tizen - Public New", publicNew.ProfileName);
        Assert.Equal(TizenDistributorChain.New, publicNew.Chain);
        Assert.True(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Public, TizenDistributorChain.New));
        Assert.True(TizenProfileStore.HasProfile(_store, TizenDistributorLevel.Public));
        Assert.NotEqual(first.ProfileDir, publicNew.ProfileDir);
    }

    [Theory]
    [InlineData("Tizen - Public", TizenDistributorLevel.Public, TizenDistributorChain.Legacy)]
    [InlineData("Tizen - Public New", TizenDistributorLevel.Public, TizenDistributorChain.New)]
    [InlineData("Tizen - Platform", TizenDistributorLevel.Platform, TizenDistributorChain.New)]
    [InlineData("Tizen - Platform Legacy", null, null)]
    [InlineData("Tizen - Public Legacy", null, null)]
    [InlineData("Jelly2Sams - Public", null, null)]
    [InlineData("Tizen - Nope", null, null)]
    [InlineData(null, null, null)]
    public void Profile_names_are_recognised(string? name, TizenDistributorLevel? level, TizenDistributorChain? chain)
    {
        Assert.Equal(level, TizenProfileStore.LevelOf(name));
        Assert.Equal(chain, TizenProfileStore.Parse(name)?.Chain);
        Assert.Equal(level is not null || name is "Tizen - Nope" or "Tizen - Platform Legacy" or "Tizen - Public Legacy", TizenProfileStore.IsTizenProfileName(name));
        if (level is { } l)
            Assert.Equal(name, TizenProfileStore.ProfileName(l, chain));
    }
}
