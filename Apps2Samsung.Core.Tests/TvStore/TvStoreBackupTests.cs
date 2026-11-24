using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Backup;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

/// <summary>
/// TV-Store's own data in Backup &amp; restore (#1179): reading what A2SStore.backup and restore
/// answer, carrying the backup untouched in a profile and in the zip, and keeping it out of the
/// automatic saved lists.
/// </summary>
[Collection("SavedTvs")]
public sealed class TvStoreBackupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("a2s-tvstorebackup-").FullName;
    private const string Tv = "192.168.1.8";
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public TvStoreBackupTests()
    {
        TvStoreBackups.Configure(Path.Combine(_root, "data", TvStoreBackups.FileName));
        // No TV in a test: a copy asks the address for TV-Store's backup unless a test answers for it.
        TvStorePublicBackup.Silence();
    }

    public void Dispose()
    {
        TvStorePublicBackup.ResetForTests();
        TvStoreBackups.Reset();
        SavedTvs.Reset();
        Directory.Delete(_root, recursive: true);
    }

    // The shape tizen-app/service/backup.js writes, cut down to what a head looks at.
    private static JsonElement Backup(bool secrets = true, string madeAt = "2026-11-24T10:00:00.000Z") => JsonSerializer.Deserialize<JsonElement>($$"""
        {
          "format": "a2s-tvstore-backup", "version": 1, "madeAt": "{{madeAt}}", "tvStore": "0.3.41",
          "tv": "UA43NU7100", "duid": "ABCDEF123", "secrets": {{(secrets ? "true" : "false")}},
          "page": { "pinned": ["Jellyfin"] },
          "service": { "sources": [ { "repositoryId": "mine" } ], "odd": [1, 2, { "kept": "as is" }] }
        }
        """);

    private static Func<string, string> Keys => key => key;

    [Fact]
    public void Describe_ReadsWhatTheBackupSaysAboutItself()
    {
        var info = TvStoreBackup.Describe(Backup(secrets: false))!;

        Assert.False(info.HasSecrets);
        Assert.Equal("2026-11-24T10:00:00.000Z", info.MadeAt);
        Assert.Equal("0.3.41", info.TvStoreVersion);
        Assert.Equal("UA43NU7100", info.Tv);
        Assert.True(TvStoreBackup.Describe(Backup())!.HasSecrets);
    }

    [Theory]
    [InlineData("""{ "apps": [] }""")]
    [InlineData("""{ "format": "something-else", "version": 1 }""")]
    [InlineData("""[1, 2]""")]
    [InlineData("\"text\"")]
    public void Describe_SomethingElse_IsNotABackup(string json) =>
        Assert.Null(TvStoreBackup.Describe(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void Describe_NoBackup_IsNull() => Assert.Null(TvStoreBackup.Describe(null));

    [Fact]
    public void BackupReply_GivesTheObject_Untouched()
    {
        var reply = JsonNode.Parse($$"""{ "ok": true, "backup": {{Backup().GetRawText()}} }""");

        var backup = TvStoreBackup.ParseBackupReply(reply);

        Assert.Equal("as is", backup.GetProperty("service").GetProperty("odd")[2].GetProperty("kept").GetString());
    }

    [Theory]
    [InlineData("""{ "ok": false, "error": "no answer from the service" }""", "no answer from the service")]
    [InlineData("""{ "ok": true }""", "no reply")]
    [InlineData("""{ "ok": true, "backup": { "format": "other" } }""", "not a TV-Store backup")]
    public void BackupReply_ThatIsNoBackup_ThrowsWithTheTvsWords(string reply, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TvStoreBackup.ParseBackupReply(JsonNode.Parse(reply)));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void RestoreReply_HasThreeOutcomes()
    {
        var restored = TvStoreBackup.ParseRestoreReply(JsonNode.Parse("""{ "ok": true, "restored": true, "applied": { "certificates": 2, "sources": 1, "userSettings": 3 } }"""));
        Assert.Equal(new TvStoreRestoreResult(TvStoreRestoreState.Restored, 2, 1, 3), restored);

        var declined = TvStoreBackup.ParseRestoreReply(JsonNode.Parse("""{ "ok": true, "restored": false, "error": "declined on the TV" }"""));
        Assert.Equal(TvStoreRestoreState.Declined, declined.State);
        Assert.Equal("declined on the TV", declined.Error);

        var refused = TvStoreBackup.ParseRestoreReply(JsonNode.Parse("""{ "ok": false, "error": "made by a newer TV-Store" }"""));
        Assert.Equal(TvStoreRestoreState.Refused, refused.State);
        Assert.Equal("made by a newer TV-Store", refused.Error);

        Assert.Equal(TvStoreRestoreState.Refused, TvStoreBackup.ParseRestoreReply(null).State);
        // A restore that does not say what it applied is still a restore.
        Assert.Null(TvStoreBackup.ParseRestoreReply(JsonNode.Parse("""{ "ok": true, "restored": true }""")).Certificates);
    }

    [Fact]
    public void Messages_ComeFromTheHeadsStrings()
    {
        Assert.Equal("statusTvStoreRestoreDone statusTvStoreRestoreApplied",
            TvStoreBackup.RestoreMessage(new TvStoreRestoreResult(TvStoreRestoreState.Restored, 2, 1, 3), Keys));
        Assert.Equal("statusTvStoreRestoreDone", TvStoreBackup.RestoreMessage(new TvStoreRestoreResult(TvStoreRestoreState.Restored), Keys));
        Assert.Equal("statusTvStoreRestoreDeclined", TvStoreBackup.RestoreMessage(new TvStoreRestoreResult(TvStoreRestoreState.Declined), Keys));
        Assert.Equal("refused: newer", TvStoreBackup.RestoreMessage(new TvStoreRestoreResult(TvStoreRestoreState.Refused, Error: "newer"), _ => "refused: {0}"));

        Assert.Equal("statusTvStoreBackupIncludedSecrets", TvStoreBackup.SavedMessage(new TvStoreBackupTaken(Backup(), TvStoreBackupSkipped.None, TvStoreBackup.Describe(Backup())), Keys));
        Assert.Equal("statusTvStoreBackupIncluded", TvStoreBackup.SavedMessage(new TvStoreBackupTaken(Backup(false), TvStoreBackupSkipped.None, TvStoreBackup.Describe(Backup(false))), Keys));
        Assert.Equal("statusTvStoreBackupTooOld", TvStoreBackup.SavedMessage(new TvStoreBackupTaken(null, TvStoreBackupSkipped.TooOld, null), Keys));
        Assert.Equal("statusTvStoreBackupNotInstalled", TvStoreBackup.SavedMessage(new TvStoreBackupTaken(null, TvStoreBackupSkipped.NotInstalled, null), Keys));
    }

    private static InstalledApp OnTv(string tizenId, string version) => new("App", null, tizenId, version, "", true);

    // Taking a backup starts TV-Store again on the TV: one that cannot answer is not asked.
    [Theory]
    [InlineData("0.3.40", TvStoreBackupSkipped.TooOld)]
    [InlineData("0.3.34", TvStoreBackupSkipped.TooOld)]
    [InlineData("0.3.41", TvStoreBackupSkipped.None)]
    [InlineData("0.4.0", TvStoreBackupSkipped.None)]
    [InlineData("", TvStoreBackupSkipped.None)]
    public void CheckOnTv_GoesByTheListedVersion(string version, TvStoreBackupSkipped expected) =>
        Assert.Equal(expected, TvStoreBackup.CheckOnTv(new[] { OnTv("Other.App", "1.0"), OnTv(TvStorePackage.AppId, version) }));

    [Fact]
    public void CheckOnTv_WithoutTvStore_IsNotInstalled() =>
        Assert.Equal(TvStoreBackupSkipped.NotInstalled, TvStoreBackup.CheckOnTv(new[] { OnTv("Other.App", "1.0") }));

    private static TvProfile Profile() => new()
    {
        SavedAtUtc = T0,
        TvName = "Living room",
        TvIp = Tv,
        Apps = { new TvProfileApp { Name = "Jellyfin", AppId = "AprZAARz4r.Jellyfin", PackageFile = "Jellyfin.wgt" } },
    };

    [Fact]
    public void AProfileFile_CarriesTheBackup_AndSaysWhenItIsPrivate()
    {
        var profile = Profile();
        TvProfiles.SetTvStore(profile, Backup());

        var read = TvProfiles.Parse(TvProfiles.Serialize(profile));

        Assert.True(read.TvStoreHasSecrets);
        Assert.True(read.IsPrivate);
        Assert.Equal(Backup().GetRawText().Replace(" ", "").Replace("\n", ""), read.TvStore!.Value.GetRawText().Replace(" ", "").Replace("\n", "").Replace("\r", ""));

        TvProfiles.SetTvStore(profile, Backup(secrets: false));
        Assert.False(TvProfiles.Parse(TvProfiles.Serialize(profile)).IsPrivate);

        // A plain list has neither field in the file.
        var plain = TvProfiles.Serialize(Profile());
        Assert.DoesNotContain("tvStore", plain);
        Assert.Null(TvProfiles.Parse(plain).TvStore);
    }

    [Fact]
    public void SetTvStore_WithSomethingElse_CarriesNothing()
    {
        var profile = Profile();
        TvProfiles.SetTvStore(profile, JsonSerializer.Deserialize<JsonElement>("""{ "format": "other" }"""));

        Assert.Null(profile.TvStore);
        Assert.False(profile.TvStoreHasSecrets);
    }

    // The saved lists are written by themselves and copied out as files: no keys in there.
    [Fact]
    public void TheAutomaticSavedList_NeverKeepsTheBackup()
    {
        SavedTvs.Configure(Path.Combine(_root, "data", SavedTvs.FileName));
        var profile = Profile();
        TvProfiles.SetTvStore(profile, Backup());

        var saved = SavedTvs.Remember(Tv, profile, T0)!;

        Assert.Null(saved.Profile.TvStore);
        Assert.False(saved.Profile.TvStoreHasSecrets);
        Assert.DoesNotContain("a2s-tvstore-backup", File.ReadAllText(Path.Combine(_root, "data", SavedTvs.FileName)));
    }

    [Fact]
    public void TheStore_KeepsOnePerTv_TheNewest()
    {
        TvStoreBackups.Set(Tv, Backup(madeAt: "first"), T0);
        TvStoreBackups.Set(Tv.ToUpperInvariant(), Backup(madeAt: "second"), T0.AddDays(1));
        TvStoreBackups.Set("192.168.1.9", JsonSerializer.Deserialize<JsonElement>("""{ "format": "other" }"""), T0);

        Assert.Equal("second", TvStoreBackup.Describe(TvStoreBackups.Get(Tv)!.Backup)!.MadeAt);
        Assert.Null(TvStoreBackups.Get("192.168.1.9"));

        TvStoreBackups.Forget(Tv);
        Assert.Null(TvStoreBackups.Get(Tv));
        Assert.Null(TvStoreBackups.ExportJson());
    }

    [Fact]
    public void TheZip_CarriesTheBackups_AndTheOtherDeviceKeepsItsNewerOne()
    {
        TvStoreBackups.Set(Tv, Backup(madeAt: "from the computer"), T0);
        TvStoreBackups.Set("192.168.1.9", Backup(madeAt: "second tv"), T0);
        using var zip = new MemoryStream();
        BackupService.Export(zip, "{}", Path.Combine(_root, "no-certificates"));

        zip.Position = 0;
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
            Assert.NotNull(archive.GetEntry(BackupService.TvStoreBackupsEntryName));

        // Another device: it has a newer backup of the first TV, and none of the second.
        TvStoreBackups.Configure(Path.Combine(_root, "phone", TvStoreBackups.FileName));
        TvStoreBackups.Set(Tv, Backup(madeAt: "from the phone"), T0.AddDays(1));
        zip.Position = 0;
        BackupService.Import(zip, Path.Combine(_root, "phone-certificates"));

        Assert.Equal("from the phone", TvStoreBackup.Describe(TvStoreBackups.Get(Tv)!.Backup)!.MadeAt);
        Assert.Equal("second tv", TvStoreBackup.Describe(TvStoreBackups.Get("192.168.1.9")!.Backup)!.MadeAt);
    }

    // The automatic copies of a TV's app list (#1177) carry the last backup taken, when it holds no keys.
    private string CopiesFolder => Path.Combine(_root, "copies");
    private string CopiesIndex => Path.Combine(_root, "data", TvBackups.IndexFileName);
    private Apps2Samsung.Certificate.FolderBackupTarget Copies => new(CopiesFolder);

    [Fact]
    public void AnAutomaticCopy_CarriesABackupWithoutSecrets_AndNeverOneWithThem()
    {
        var lean = TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime, Backup(secrets: false))!;
        var read = TvProfiles.Parse(File.ReadAllText(Path.Combine(CopiesFolder, lean.FileName)));
        Assert.NotNull(read.TvStore);
        Assert.False(read.IsPrivate);

        var full = TvBackups.BackUp(CopiesIndex, Copies, "192.168.1.9", Profile(), T0.UtcDateTime, Backup(secrets: true))!;
        var text = File.ReadAllText(Path.Combine(CopiesFolder, full.FileName));
        Assert.DoesNotContain("a2s-tvstore-backup", text);
        Assert.Null(TvProfiles.Parse(text).TvStore);
    }

    [Fact]
    public void ANewerBackup_IsANewCopy_TheSameOneIsNot()
    {
        Assert.NotNull(TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime));
        Assert.NotNull(TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime.AddHours(1), Backup(secrets: false, madeAt: "one")));
        Assert.Null(TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime.AddHours(2), Backup(secrets: false, madeAt: "one")));
        Assert.NotNull(TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime.AddHours(3), Backup(secrets: false, madeAt: "two")));
        // A backup with keys changes nothing about the file: it is as if there were none.
        Assert.Null(TvBackups.BackUp(CopiesIndex, Copies, Tv, Profile(), T0.UtcDateTime.AddHours(4), Backup(secrets: false, madeAt: "two")));
        Assert.Equal(3, Directory.GetFiles(CopiesFolder).Length);
    }

    // A list handed in with a backup of its own does not smuggle it into the file.
    [Fact]
    public void AnAutomaticCopy_TakesTheBackupOnlyFromTheStore()
    {
        var profile = Profile();
        TvProfiles.SetTvStore(profile, Backup(secrets: true));

        var entry = TvBackups.BackUp(CopiesIndex, Copies, Tv, profile, T0.UtcDateTime)!;

        Assert.DoesNotContain("a2s-tvstore-backup", File.ReadAllText(Path.Combine(CopiesFolder, entry.FileName)));
    }

    [Fact]
    public void TakingABackup_WritesTheAutomaticCopyAgain()
    {
        SavedTvs.Configure(Path.Combine(_root, "data", SavedTvs.FileName));
        TvBackups.Configure(CopiesIndex, Copies);
        try
        {
            SavedTvs.Remember(Tv, Profile(), T0);
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));
            Assert.Single(Directory.GetFiles(CopiesFolder));

            TvStoreBackups.Set(Tv, Backup(secrets: false), T0.AddHours(1));
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));

            var newest = TvBackups.Status()!.LatestFor(Tv)!;
            Assert.Equal(2, Directory.GetFiles(CopiesFolder).Length);
            Assert.NotNull(TvProfiles.Parse(File.ReadAllText(newest.Location)).TvStore);
        }
        finally
        {
            TvBackups.Reset();
        }
    }

    [Fact]
    public void WithoutABackup_TheZipHasNoEntryForIt()
    {
        using var zip = new MemoryStream();
        BackupService.Export(zip, "{}", Path.Combine(_root, "no-certificates"));

        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        Assert.Null(archive.GetEntry(BackupService.TvStoreBackupsEntryName));
    }

    // ---- The backup TV-Store hands out by itself (TV-Store#149, GET /backup/public) ----

    private const string RelayWithRoute = """{ "ok": true, "relay": { "enabled": false }, "backupPublic": true }""";
    private const string RelayWithout = """{ "ok": true, "relay": { "enabled": false } }""";

    private static void TvAnswers(string? relay, int status, string? body, Action? asked = null)
    {
        TvStorePublicBackup.ReadRelay = (_, _) => Task.FromResult(relay);
        TvStorePublicBackup.ReadBackup = (_, _) =>
        {
            asked?.Invoke();
            return Task.FromResult<(int Status, string? Body)?>((status, body));
        };
    }

    [Fact]
    public void TheRelayReport_SaysWhetherTheRouteExists()
    {
        Assert.True(TvStorePublicBackup.SaysSupported(RelayWithRoute));
        Assert.False(TvStorePublicBackup.SaysSupported(RelayWithout));
        Assert.False(TvStorePublicBackup.SaysSupported("""{ "backupPublic": "yes" }"""));
        Assert.False(TvStorePublicBackup.SaysSupported("not json"));
        Assert.False(TvStorePublicBackup.SaysSupported(null));
    }

    [Fact]
    public void OnlyA200WithABackupWithoutKeys_IsABackup()
    {
        Assert.NotNull(TvStorePublicBackup.Parse(200, Backup(secrets: false).GetRawText()));
        Assert.Null(TvStorePublicBackup.Parse(200, Backup(secrets: true).GetRawText()));
        Assert.Null(TvStorePublicBackup.Parse(409, """{ "ok": false, "code": "NO_SCREEN_STATE", "error": "not yet" }"""));
        Assert.Null(TvStorePublicBackup.Parse(403, "192.168.1.20 is not one of the devices picked"));
        Assert.Null(TvStorePublicBackup.Parse(200, """{ "format": "other" }"""));
        Assert.Null(TvStorePublicBackup.Parse(200, "<html>"));
    }

    [Fact]
    public async Task AnOlderTvStore_IsNotAskedForTheBackup()
    {
        var asked = 0;
        TvAnswers(RelayWithout, 200, Backup(secrets: false).GetRawText(), () => asked++);
        Assert.Null(await TvStorePublicBackup.FetchAsync(Tv));

        TvAnswers(null, 200, Backup(secrets: false).GetRawText(), () => asked++);
        Assert.Null(await TvStorePublicBackup.FetchAsync(Tv));

        // A saved list keyed by something that is no address.
        TvAnswers(RelayWithRoute, 200, Backup(secrets: false).GetRawText(), () => asked++);
        Assert.Null(await TvStorePublicBackup.FetchAsync("Living room"));
        Assert.Equal(0, asked);

        Assert.NotNull(await TvStorePublicBackup.FetchAsync(Tv));
        Assert.Equal(1, asked);
    }

    [Fact]
    public void TheBackupFromTheTv_IsKeptApart_AndOnlyWhenItChanged()
    {
        TvStoreBackups.Set(Tv, Backup(secrets: true, madeAt: "by hand"), T0);

        Assert.True(TvStoreBackups.KeepFromTv(Tv, Backup(secrets: false, madeAt: "one"), T0.AddHours(1)));
        Assert.False(TvStoreBackups.KeepFromTv(Tv, Backup(secrets: false, madeAt: "one"), T0.AddHours(2)));
        Assert.False(TvStoreBackups.KeepFromTv(Tv, Backup(secrets: true, madeAt: "two"), T0.AddHours(3)));
        Assert.True(TvStoreBackups.KeepFromTv(Tv, Backup(secrets: false, madeAt: "two"), T0.AddHours(4)));

        // The one somebody took, keys and all, is still the one a restore and the zip get.
        Assert.Equal("by hand", TvStoreBackup.Describe(TvStoreBackups.Get(Tv)!.Backup)!.MadeAt);
        Assert.Equal("two", TvStoreBackup.Describe(TvStoreBackups.GetFromTv(Tv)!.Backup)!.MadeAt);
        Assert.DoesNotContain("fromTv", TvStoreBackups.ExportJson());

        TvStoreBackups.Forget(Tv);
        Assert.Null(TvStoreBackups.Get(Tv));
        Assert.Null(TvStoreBackups.GetFromTv(Tv));
    }

    [Fact]
    public void AnAutomaticCopy_AsksTheTv_AndWritesAgainOnlyWhenTvStoreChanged()
    {
        SavedTvs.Configure(Path.Combine(_root, "data", SavedTvs.FileName));
        TvBackups.Configure(CopiesIndex, Copies);
        try
        {
            // Somebody took a backup with keys before: that one never goes into a copy.
            TvStoreBackups.Set(Tv, Backup(secrets: true, madeAt: "by hand"), T0);
            TvAnswers(RelayWithRoute, 200, Backup(secrets: false, madeAt: "one").GetRawText());

            SavedTvs.Remember(Tv, Profile(), T0);
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));
            var first = TvProfiles.Parse(File.ReadAllText(TvBackups.Status()!.LatestFor(Tv)!.Location));
            Assert.Equal("one", TvStoreBackup.Describe(first.TvStore)!.MadeAt);
            Assert.False(first.TvStoreHasSecrets);
            Assert.Single(Directory.GetFiles(CopiesFolder));

            // The same answer, then no answer at all (TV-Store is not running): nothing new.
            SavedTvs.Remember(Tv, Profile(), T0.AddHours(1));
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));
            TvStorePublicBackup.Silence();
            SavedTvs.Remember(Tv, Profile(), T0.AddHours(2));
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));
            Assert.Single(Directory.GetFiles(CopiesFolder));

            // A pin changed on the TV.
            TvAnswers(RelayWithRoute, 200, Backup(secrets: false, madeAt: "two").GetRawText());
            SavedTvs.Remember(Tv, Profile(), T0.AddHours(3));
            TvBackups.WaitForPending(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Directory.GetFiles(CopiesFolder).Length);
            Assert.Equal("two", TvStoreBackup.Describe(TvProfiles.Parse(File.ReadAllText(TvBackups.Status()!.LatestFor(Tv)!.Location)).TvStore)!.MadeAt);
        }
        finally
        {
            TvBackups.Reset();
        }
    }
}
