using Apps2Samsung.Catalog;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Tests.Catalog;

/// <summary>
/// Automatic copies of each TV's app list (#1177): when a file is written, what is in it, what
/// pruning keeps per TV, and that a deleted file counts as no copy.
/// </summary>
// With the saved-TV tests: both point the one saved-TV store at a folder of their own.
[Collection("SavedTvs")]
public sealed class TvBackupsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("a2s-tvbackup-").FullName;
    private string Index => Path.Combine(_root, "data", TvBackups.IndexFileName);
    private string Folder => Path.Combine(_root, "backups");
    private FolderBackupTarget Target => new(Folder);
    private static readonly DateTime T0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private const string Tv = "192.168.1.8";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TvProfileApp App(string name, string version = "1.0", string? variant = null, string source = "official") => new()
    {
        Name = name,
        AppId = $"Pkg{name}.{name}",
        PackageFile = $"{name}-{version}.wgt",
        Variant = variant,
        Source = source,
        Version = version,
    };

    private static TvProfile Profile(params TvProfileApp[] apps) => new()
    {
        SavedAtUtc = new DateTimeOffset(T0),
        TvName = "[TV] Samsung 7 Series (43)",
        TvIp = Tv,
        Apps = apps.ToList(),
    };

    [Fact]
    public void FirstCopy_IsAProfileFile_RestoreFromAFileReads()
    {
        var entry = TvBackups.BackUp(Index, Target, Tv, Profile(App("Jellyfin"), App("Litefin", variant: "Legacy")), T0);

        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Apps);
        Assert.StartsWith(TvBackups.FilePrefix + "TV-Samsung-7-Series-43-", entry.FileName);
        Assert.EndsWith(TvBackups.FileExtension, entry.FileName);

        var read = TvProfiles.Parse(File.ReadAllText(Path.Combine(Folder, entry.FileName)));
        Assert.Equal(new[] { "Jellyfin", "Litefin" }, read.Apps.Select(a => a.Name));
        Assert.Equal("Legacy", read.Apps[1].Variant);
    }

    [Fact]
    public void TheSameApps_AreNotCopiedAgain_NotForANewVersionEither()
    {
        Assert.NotNull(TvBackups.BackUp(Index, Target, Tv, Profile(App("Jellyfin"), App("Litefin")), T0));

        Assert.Null(TvBackups.BackUp(Index, Target, Tv, Profile(App("Litefin"), App("Jellyfin")), T0.AddHours(1)));
        Assert.Null(TvBackups.BackUp(Index, Target, Tv, Profile(App("Jellyfin", version: "2.0"), App("Litefin")), T0.AddHours(2)));
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public void AnotherApp_AnotherVariant_OrAnotherSource_IsANewCopy()
    {
        TvBackups.BackUp(Index, Target, Tv, Profile(App("Litefin", variant: "Normal")), T0);

        Assert.NotNull(TvBackups.BackUp(Index, Target, Tv, Profile(App("Litefin", variant: "Legacy")), T0.AddMinutes(1)));
        Assert.NotNull(TvBackups.BackUp(Index, Target, Tv, Profile(App("Litefin", variant: "Legacy"), App("Jellyfin")), T0.AddMinutes(2)));
        Assert.NotNull(TvBackups.BackUp(Index, Target, Tv, Profile(App("Litefin", variant: "Legacy"), App("Jellyfin", source: "unverified:mine")), T0.AddMinutes(3)));
        Assert.Equal(4, Directory.GetFiles(Folder).Length);
    }

    [Fact]
    public void AnEmptyList_IsNeverCopied()
    {
        Assert.Null(TvBackups.BackUp(Index, Target, Tv, Profile(), T0));
        Assert.False(Directory.Exists(Folder));
    }

    // The folder is one people sync, and the values can be server addresses and keys.
    [Fact]
    public void TypedValues_NeverReachTheFile()
    {
        var app = App("Jellyfin");
        app.Settings = new Dictionary<string, string> { ["server"] = "https://jellyfin.example", ["token"] = "s3cret" };
        var profile = Profile(app);
        profile.ContainsSettings = true;

        var entry = TvBackups.BackUp(Index, Target, Tv, profile, T0)!;

        var text = File.ReadAllText(Path.Combine(Folder, entry.FileName));
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain("jellyfin.example", text);
        Assert.False(TvProfiles.Parse(text).ContainsSettings);
        // The caller's list is left as it was.
        Assert.NotNull(app.Settings);
    }

    [Fact]
    public void TheLastFive_OfEachTv_AreKept()
    {
        for (var i = 0; i < TvBackups.Keep + 3; i++)
            TvBackups.BackUp(Index, Target, Tv, Profile(Enumerable.Range(0, i + 1).Select(n => App($"App{n}")).ToArray()), T0.AddMinutes(i));
        TvBackups.BackUp(Index, Target, "192.168.1.9", Profile(App("Jellyfin")), T0);

        var status = new TvBackupStatus(Directory.GetFiles(Folder).Select(f => Path.GetFileName(f)!).Select(f => new TvBackupEntry("", "", f, "", T0, "", 0)).ToList(), Folder, null);
        Assert.Equal(TvBackups.Keep + 1, status.Entries.Count);

        // The newest of the first TV is the list with all eight apps.
        var newest = Directory.GetFiles(Folder).Select(f => TvProfiles.Parse(File.ReadAllText(f))).Max(p => p.Apps.Count);
        Assert.Equal(TvBackups.Keep + 3, newest);
    }

    [Fact]
    public void ADeletedFile_IsNoCopy_TheNextReadWritesItAgain()
    {
        var first = TvBackups.BackUp(Index, Target, Tv, Profile(App("Jellyfin")), T0)!;
        File.Delete(Path.Combine(Folder, first.FileName));

        var again = TvBackups.BackUp(Index, Target, Tv, Profile(App("Jellyfin")), T0.AddDays(1));

        Assert.NotNull(again);
        Assert.True(File.Exists(Path.Combine(Folder, again!.FileName)));
    }

    // A TV that lost its apps keeps them in the saved list (SavedTvs), so its copy does not shrink.
    [Fact]
    public void AWipedTv_DoesNotGetASmallerCopy()
    {
        var saved = Path.Combine(_root, "data", SavedTvs.FileName);
        SavedTvs.Configure(saved);
        try
        {
            var before = SavedTvs.Remember(Tv, Profile(App("Jellyfin"), App("Litefin")), new DateTimeOffset(T0))!;
            Assert.NotNull(TvBackups.BackUp(Index, Target, Tv, before.Profile, T0));

            var after = SavedTvs.Remember(Tv, Profile(), new DateTimeOffset(T0.AddDays(1)))!;

            Assert.Equal(2, after.Missing.Count);
            Assert.Null(TvBackups.BackUp(Index, Target, Tv, after.Profile, T0.AddDays(1)));
            Assert.Single(Directory.GetFiles(Folder));
        }
        finally
        {
            SavedTvs.Reset();
        }
    }

    [Fact]
    public void Remember_CopiesTheListOut_ByItself()
    {
        SavedTvs.Configure(Path.Combine(_root, "data", SavedTvs.FileName));
        TvBackups.Configure(Index, Target);
        try
        {
            SavedTvs.Remember(Tv, Profile(App("Jellyfin")), new DateTimeOffset(T0));
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));

            var status = TvBackups.Status()!;
            Assert.Equal(Folder, status.DisplayLocation);
            Assert.Null(status.LastError);
            Assert.Equal(1, status.LatestFor(Tv)!.Apps);
            Assert.True(File.Exists(status.LatestFor(Tv)!.Location));
        }
        finally
        {
            TvBackups.Reset();
            SavedTvs.Reset();
        }
    }
}
