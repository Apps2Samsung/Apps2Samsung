using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Certificate;

/// <summary>
/// Automatic certificate backups (#1068): when one is written, what counts as backed up, what pruning
/// keeps, and which backups hold an older author certificate.
/// </summary>
public sealed class CertificateBackupsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("a2s-certbackup-").FullName;
    private string Store => Path.Combine(_root, "store");
    private string Index => Path.Combine(_root, "data", CertificateBackups.IndexFileName);
    private string Folder => Path.Combine(_root, "backups");
    private static readonly DateTime T0 = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Pkcs12(string subject, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return cert.Export(X509ContentType.Pkcs12, password);
    }

    private string Profile(string name, byte[]? author = null, byte[]? distributor = null)
    {
        var dir = Path.Combine(Store, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "author.p12"), author ?? Pkcs12("CN=Jelly2Sams", "pw"));
        File.WriteAllBytes(Path.Combine(dir, "distributor.p12"), distributor ?? Pkcs12("CN=TizenSDK", "pw"));
        File.WriteAllText(Path.Combine(dir, "password.txt"), "pw\n");
        return dir;
    }

    private FolderBackupTarget Target => new(Folder);

    [Fact]
    public void FirstBackup_HoldsEveryUsableProfile_TizenToo()
    {
        Profile("Jelly2Sams - Public");
        Profile("Tizen - Public");

        var entry = CertificateBackups.BackUp(Store, Index, Target, T0, force: false);

        Assert.NotNull(entry);
        Assert.True(File.Exists(entry!.Location));
        Assert.StartsWith(CertificateBackups.FilePrefix, entry.FileName);
        Assert.EndsWith(CertificateArchive.FileExtension, entry.FileName);
        Assert.Equal(new[] { "Jelly2Sams - Public", "Tizen - Public" }, entry.Profiles.Select(p => p.ProfileName));

        using var stream = File.OpenRead(entry.Location);
        var preview = CertificateArchive.Preview(stream, Path.Combine(_root, "elsewhere"));
        Assert.Equal(2, preview.Profiles.Count);

        var status = CertificateBackups.Status(Store, Index, Target);
        Assert.True(status.AllBackedUp);
        Assert.Equal(entry.CreatedUtc, status.Latest!.CreatedUtc);
    }

    [Fact]
    public void NothingChanged_NoSecondBackup_UnlessForced()
    {
        Profile("Jelly2Sams - Public");
        CertificateBackups.BackUp(Store, Index, Target, T0, force: false);

        Assert.Null(CertificateBackups.BackUp(Store, Index, Target, T0.AddMinutes(1), force: false));
        Assert.NotNull(CertificateBackups.BackUp(Store, Index, Target, T0.AddMinutes(2), force: true));
        Assert.Equal(2, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public void RenewedDistributor_CountsAsNotBackedUp_UntilTheNextBackup()
    {
        var dir = Profile("Jelly2Sams - Public");
        CertificateBackups.BackUp(Store, Index, Target, T0, force: false);

        File.WriteAllBytes(Path.Combine(dir, "distributor.p12"), Pkcs12("CN=TizenSDK", "pw"));
        var status = CertificateBackups.Status(Store, Index, Target);
        Assert.False(status.AllBackedUp);
        Assert.Single(status.NotBackedUp);

        Assert.NotNull(CertificateBackups.BackUp(Store, Index, Target, T0.AddDays(1), force: false));
        Assert.True(CertificateBackups.Status(Store, Index, Target).AllBackedUp);
    }

    [Fact]
    public void DeletedBackupFile_CountsAsNotBackedUp()
    {
        Profile("Jelly2Sams - Public");
        var entry = CertificateBackups.BackUp(Store, Index, Target, T0, force: false)!;

        File.Delete(entry.Location);

        Assert.False(CertificateBackups.Status(Store, Index, Target).AllBackedUp);
        Assert.NotNull(CertificateBackups.BackUp(Store, Index, Target, T0.AddMinutes(1), force: false));
    }

    [Fact]
    public void NoProfile_NoBackup()
    {
        Assert.Null(CertificateBackups.BackUp(Store, Index, Target, T0, force: true));
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public void Prune_KeepsFive_ButNeverTheLastCopyOfAnOldAuthor()
    {
        var dir = Profile("Jelly2Sams - Public");
        var first = CertificateBackups.BackUp(Store, Index, Target, T0, force: false)!;

        // A new author certificate: the first backup is now the only one holding the old author.
        File.WriteAllBytes(Path.Combine(dir, "author.p12"), Pkcs12("CN=Jelly2Sams", "pw"));
        var second = CertificateBackups.BackUp(Store, Index, Target, T0.AddDays(1), force: false)!;
        for (var i = 2; i < 8; i++)
            CertificateBackups.BackUp(Store, Index, Target, T0.AddDays(i), force: true);

        Assert.True(File.Exists(first.Location));   // last copy of the old author: kept
        Assert.False(File.Exists(second.Location)); // same author as the newer five: pruned
        Assert.Equal(CertificateBackups.Keep + 1, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public void OtherAuthors_FindsTheBackupWithTheOlderCertificate()
    {
        var dir = Profile("Jelly2Sams - Public");
        CertificateBackups.Configure(Store, Index, Target);
        try
        {
            var old = CertificateBackups.BackUpNow()!;
            File.WriteAllBytes(Path.Combine(dir, "author.p12"), Pkcs12("CN=Jelly2Sams", "pw"));
            CertificateBackups.Changed();

            var others = CertificateBackups.OtherAuthors("Jelly2Sams - Public");

            Assert.Single(others);
            Assert.Equal(old.Location, others[0].Location);
            Assert.Empty(CertificateBackups.OtherAuthors("Tizen - Public"));
        }
        finally
        {
            CertificateBackups.Reset();
        }
    }

    [Fact]
    public void Import_IsBackedUpByItself_WhenConfigured()
    {
        var source = Path.Combine(_root, "source");
        var dir = Path.Combine(source, "Jelly2Sams - Public");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "author.p12"), Pkcs12("CN=Jelly2Sams", "pw"));
        File.WriteAllBytes(Path.Combine(dir, "distributor.p12"), Pkcs12("CN=TizenSDK", "pw"));
        File.WriteAllText(Path.Combine(dir, "password.txt"), "pw");
        using var archive = new MemoryStream();
        CertificateArchive.Export(archive, source);
        archive.Position = 0;

        CertificateBackups.Configure(Store, Index, Target);
        try
        {
            CertificateArchive.Import(archive, Store);
            Assert.True(CertificateBackups.Status()!.AllBackedUp);
            Assert.Single(Directory.GetFiles(Folder));
        }
        finally
        {
            CertificateBackups.Reset();
        }
    }
}
