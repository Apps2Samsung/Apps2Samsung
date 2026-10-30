using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Tests.Certificate;

/// <summary>
/// The cert-only archive (#921): what goes in, what the preview says before anything is written,
/// what an import refuses, and the preference afterwards.
/// </summary>
public sealed class CertificateArchiveTests : IDisposable
{
    private const string Duid = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string OtherDuid = "0123456789ABCDEF0123456789ABCDEF01234567";

    private readonly string _root = Directory.CreateTempSubdirectory("a2s-archive-").FullName;
    private string SourceStore => Path.Combine(_root, "source");
    private string TargetStore => Path.Combine(_root, "target");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static X509Certificate2 Author(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Jelly2Sams", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static X509Certificate2 Distributor(string email, params string[] duids)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN=TizenSDK, E={email}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var duid in duids)
            san.AddUri(new Uri("URN:tizen:deviceid=" + duid));
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static string Profile(string store, CertificatePrivilegeLevel level, X509Certificate2 author, X509Certificate2 distributor, string password = "pw")
    {
        var dir = Path.Combine(store, CertificateProvisioningService.ProfileName(level));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "author.p12"), author.Export(X509ContentType.Pkcs12, password));
        File.WriteAllBytes(Path.Combine(dir, "distributor.p12"), distributor.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(Path.Combine(dir, "password.txt"), password + "\n");
        File.WriteAllText(Path.Combine(dir, "signed_author.cer"), "cer");
        return dir;
    }

    private static MemoryStream Exported(string store, CertificatePrivilegeLevel? level = null)
    {
        var stream = new MemoryStream();
        CertificateArchive.Export(stream, store, level);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ExportThenImport_CarriesTheProfileOver_AndPreviewSaysWhatIsInIt()
    {
        using var author = Author(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var distributor = Distributor("user@example.com", Duid, OtherDuid);
        Profile(SourceStore, CertificatePrivilegeLevel.Public, author, distributor);

        using var archive = Exported(SourceStore);

        var preview = CertificateArchive.Preview(archive, TargetStore);
        var profile = Assert.Single(preview.Profiles);
        Assert.Equal(CertificatePrivilegeLevel.Public, profile.Level);
        Assert.Equal("user@example.com", profile.AccountEmail);
        Assert.Equal(CertificateValidityState.Valid, profile.State);
        Assert.Equal(new[] { OtherDuid, Duid }, profile.Duids);
        Assert.True(preview.Covers(Duid));
        Assert.False(preview.Covers("FFFF"));
        Assert.Empty(preview.Replaces);
        Assert.Equal(4, profile.FileCount);

        archive.Position = 0;
        var result = CertificateArchive.Import(archive, TargetStore);

        Assert.Equal(4, result.FileCount);
        var dir = Path.Combine(TargetStore, "Jelly2Sams - Public");
        Assert.True(CertificateProvisioningService.HasUsableAuthorCert(dir));
        Assert.Contains(Duid, CertificateProvisioningService.GetCoveredDuids(dir));
        Assert.Equal(CertificatePreference.Public, CertificateArchive.PreferenceAfterImport(CertificatePreference.Automatic, TargetStore));
    }

    [Fact]
    public void ExportHoldsNoSettings_AndOneLevelWhenAsked()
    {
        using var author = Author(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var distributor = Distributor("user@example.com", Duid);
        Profile(SourceStore, CertificatePrivilegeLevel.Public, author, distributor);
        Profile(SourceStore, CertificatePrivilegeLevel.Partner, author, distributor);

        using var partnerOnly = Exported(SourceStore, CertificatePrivilegeLevel.Partner);
        using var zip = new ZipArchive(partnerOnly, ZipArchiveMode.Read);

        Assert.All(zip.Entries, e => Assert.StartsWith("Certificate/Jelly2Sams - Partner/", e.FullName));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("settings.json"));
    }

    [Fact]
    public void ExportRefusesAnEmptyOrExpiredStore()
    {
        Assert.Throws<CertificateArchiveException>(() => Exported(SourceStore));

        using var expired = Author(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
        using var distributor = Distributor("user@example.com", Duid);
        Profile(SourceStore, CertificatePrivilegeLevel.Public, expired, distributor);

        Assert.Throws<CertificateArchiveException>(() => Exported(SourceStore));
    }

    [Fact]
    public void ImportRefusesAnExpiredAuthor_AndLeavesTheStoreUntouched()
    {
        using var expired = Author(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
        using var distributor = Distributor("user@example.com", Duid);
        var dir = Profile(SourceStore, CertificatePrivilegeLevel.Public, expired, distributor);

        // Export does not take an expired profile, so build the archive the way a stale backup would be.
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                zip.CreateEntryFromFile(file, "Certificate/Jelly2Sams - Public/" + Path.GetFileName(file));
        }
        archive.Position = 0;

        var preview = CertificateArchive.Preview(archive, TargetStore);
        Assert.Equal(CertificateValidityState.Expired, Assert.Single(preview.Profiles).State);
        Assert.Single(preview.Rejected);

        archive.Position = 0;
        var ex = Assert.Throws<CertificateArchiveException>(() => CertificateArchive.Import(archive, TargetStore));
        Assert.Contains("expired", ex.Message);
        Assert.False(Directory.Exists(TargetStore));
    }

    [Fact]
    public void ImportReplacesTheProfileWhole_AndPreviewNamesWhatItReplaces()
    {
        using var oldAuthor = Author(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddMonths(6));
        using var newAuthor = Author(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var oldDistributor = Distributor("old@example.com", OtherDuid);
        using var newDistributor = Distributor("new@example.com", Duid);
        var existing = Profile(TargetStore, CertificatePrivilegeLevel.Public, oldAuthor, oldDistributor);
        File.WriteAllText(Path.Combine(existing, "leftover.txt"), "from the old profile");
        Profile(SourceStore, CertificatePrivilegeLevel.Public, newAuthor, newDistributor);

        using var archive = Exported(SourceStore);
        var preview = CertificateArchive.Preview(archive, TargetStore);
        Assert.Equal("old@example.com", Assert.Single(preview.Replaces).AccountEmail);

        archive.Position = 0;
        CertificateArchive.Import(archive, TargetStore);

        Assert.False(File.Exists(Path.Combine(existing, "leftover.txt")));
        Assert.Equal("new@example.com", CertificateArchive.DescribeDirectory(existing)!.AccountEmail);
        Assert.Contains(Duid, CertificateProvisioningService.GetCoveredDuids(existing));
        Assert.DoesNotContain(OtherDuid, CertificateProvisioningService.GetCoveredDuids(existing));
    }

    [Fact]
    public void ImportIgnoresEntriesThatEscapeTheStore()
    {
        using var author = Author(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var distributor = Distributor("user@example.com", Duid);
        var dir = Profile(SourceStore, CertificatePrivilegeLevel.Public, author, distributor);

        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
                zip.CreateEntryFromFile(file, "Certificate/Jelly2Sams - Public/" + Path.GetFileName(file));
            var evil = zip.CreateEntry("Certificate/Jelly2Sams - Public/../../escaped.txt");
            using (var w = new StreamWriter(evil.Open()))
                w.Write("no");
        }
        archive.Position = 0;

        CertificateArchive.Import(archive, TargetStore);

        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(TargetStore, "escaped.txt")));
        Assert.True(CertificateProvisioningService.HasUsableAuthorCert(Path.Combine(TargetStore, "Jelly2Sams - Public")));
    }

    [Fact]
    public void AFullBackupZipReadsAsACertificateArchive()
    {
        using var author = Author(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var distributor = Distributor("user@example.com", Duid);
        Profile(SourceStore, CertificatePrivilegeLevel.Partner, author, distributor);

        using var backup = new MemoryStream();
        Apps2Samsung.Backup.BackupService.Export(backup, "{\"DarkMode\":true}", SourceStore);
        backup.Position = 0;

        var preview = CertificateArchive.Preview(backup, TargetStore);
        Assert.Equal(CertificatePrivilegeLevel.Partner, Assert.Single(preview.Profiles).Level);
    }

    [Fact]
    public void SummaryNamesLevelAccountValidityCoverageAndReplacement()
    {
        using var author = Author(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var distributor = Distributor("user@example.com", Duid);
        Profile(SourceStore, CertificatePrivilegeLevel.Public, author, distributor);
        Profile(TargetStore, CertificatePrivilegeLevel.Public, author, distributor);

        using var archive = Exported(SourceStore);
        var preview = CertificateArchive.Preview(archive, TargetStore);

        var text = CertificateArchive.Summarize(preview, OtherDuid, key => key switch
        {
            "certLevelPublic" => "Public",
            "certArchiveProfileLine" => "{0}: {1}, valid {2} to {3}, covers {4} TV(s)",
            "certArchiveNotCoversThisTv" => "not covered",
            "certArchiveReplaces" => "replaces {0} ({1}, valid to {2})",
            _ => key,
        });

        Assert.Equal(
            "Public: user@example.com, valid 2026-01-01 to 2027-01-01, covers 1 TV(s)\nnot covered\nreplaces Public (user@example.com, valid to 2027-01-01)",
            text);
    }
}
