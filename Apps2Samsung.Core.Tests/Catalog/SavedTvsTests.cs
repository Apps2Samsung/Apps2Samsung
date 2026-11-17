using Apps2Samsung.Catalog;

namespace Apps2Samsung.Tests.Catalog;

// The app list a head keeps per TV by itself, and what counts as missing (#1069).
[Collection("SavedTvs")]
public class SavedTvsTests : IDisposable
{
    private const string Ip = "192.168.1.50";
    private static readonly DateTimeOffset Day1 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a2s-savedtvs-" + Guid.NewGuid().ToString("N"));

    public SavedTvsTests() => SavedTvs.Configure(Path.Combine(_dir, SavedTvs.FileName));

    public void Dispose()
    {
        SavedTvs.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TvProfileApp App(string name, string version = "1.0.0", string source = "official") =>
        new() { Name = name, AppId = name + ".app", PackageFile = name + ".wgt", Version = version, Source = source };

    private static TvProfile Profile(params TvProfileApp[] apps) =>
        new() { TvName = "Living room", TvIp = Ip, Apps = apps.ToList() };

    [Fact]
    public void An_empty_tv_that_was_never_saved_is_not_kept()
    {
        Assert.Null(SavedTvs.Remember(Ip, Profile(), Day1));
        Assert.Empty(SavedTvs.All());
    }

    [Fact]
    public void The_first_read_saves_the_list_and_nothing_is_missing()
    {
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day1);

        Assert.NotNull(saved);
        Assert.Empty(saved!.Missing);
        Assert.False(saved.OfferRestore);
        Assert.Equal(2, SavedTvs.Get(Ip)!.Profile.Apps.Count);
        Assert.Equal(Day1, SavedTvs.Get(Ip)!.Profile.SavedAtUtc);
    }

    [Fact]
    public void A_list_that_only_grew_replaces_the_saved_one()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day1);
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin", "2.0.0"), App("Litefin")), Day2)!;

        Assert.Empty(saved.Missing);
        Assert.Equal(Day2, saved.Profile.SavedAtUtc);
        Assert.Equal("2.0.0", saved.Profile.Apps.Single(a => a.Name == "Jellyfin").Version);
    }

    [Fact]
    public void Apps_gone_from_the_tv_stay_saved_and_are_missing()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin"), App("Moonlight")), Day1);
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day2)!;

        Assert.Equal(2, saved.Missing.Count);
        Assert.True(saved.OfferRestore);
        Assert.Equal(3, saved.Profile.Apps.Count);
        // The date stays the day the list was last complete.
        Assert.Equal(Day1, saved.Profile.SavedAtUtc);
        Assert.Equal(Day2, saved.LastSeenUtc);
    }

    [Fact]
    public void A_reset_tv_keeps_the_whole_list()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day1);
        var saved = SavedTvs.Remember(Ip, Profile(), Day2)!;

        Assert.Equal(2, saved.Missing.Count);
        Assert.Equal(2, saved.Profile.Apps.Count);
    }

    [Fact]
    public void A_new_app_is_added_while_others_are_missing()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day1);
        var saved = SavedTvs.Remember(Ip, Profile(App("Moonlight")), Day2)!;

        Assert.Equal(3, saved.Profile.Apps.Count);
        Assert.Equal(2, saved.Missing.Count);
    }

    [Fact]
    public void Once_everything_is_back_nothing_is_missing()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day1);
        SavedTvs.Remember(Ip, Profile(), Day2);
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day2)!;

        Assert.Empty(saved.Missing);
        Assert.Null(saved.Dismissed);
    }

    [Fact]
    public void Not_now_hides_the_offer_until_other_apps_go_missing()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin"), App("Moonlight")), Day1);
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day2);
        SavedTvs.Dismiss(Ip);
        Assert.False(SavedTvs.Get(Ip)!.OfferRestore);

        // The same app still missing: still quiet.
        Assert.False(SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day2)!.OfferRestore);
        // Another one gone: the offer is back.
        Assert.True(SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day2)!.OfferRestore);
    }

    [Fact]
    public void An_app_removed_through_the_app_is_not_missing()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Litefin")), Day1);
        SavedTvs.RemoveApp(Ip, "Litefin", "official");
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day2)!;

        Assert.Empty(saved.Missing);
        Assert.Single(saved.Profile.Apps);
    }

    [Fact]
    public void The_same_name_from_another_source_is_another_app()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin"), App("Jellyfin", source: "repo:example.org")), Day1);
        var saved = SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day2)!;

        Assert.Single(saved.Missing);
        Assert.Equal("repo:example.org", saved.Profile.Apps.Single(a => saved.Missing.Contains(SavedTvs.AppKey(a))).Source);
    }

    [Fact]
    public void Typed_values_are_never_stored()
    {
        var app = App("StreamVault");
        app.Settings = new Dictionary<string, string> { ["server"] = "https://example.test", ["key"] = "secret" };
        var profile = Profile(app);
        profile.ContainsSettings = true;

        SavedTvs.Remember(Ip, profile, Day1);

        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_dir, SavedTvs.FileName)));
        Assert.False(SavedTvs.Get(Ip)!.Profile.ContainsSettings);
    }

    [Fact]
    public void Forget_drops_the_tv_and_tvs_are_kept_apart()
    {
        SavedTvs.Remember(Ip, Profile(App("Jellyfin")), Day1);
        SavedTvs.Remember("192.168.1.51", Profile(App("Litefin")), Day1);
        SavedTvs.Forget(Ip);

        Assert.Null(SavedTvs.Get(Ip));
        Assert.Single(SavedTvs.All());
    }

    [Fact]
    public void An_unreadable_file_reads_as_no_saved_tvs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, SavedTvs.FileName), "{ not json");
        Assert.Empty(SavedTvs.All());
    }
}

// The saved lists travel in the Backup & restore zip (#1069).
[Collection("SavedTvs")]
public class SavedTvsBackupTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a2s-savedtvs-zip-" + Guid.NewGuid().ToString("N"));

    public SavedTvsBackupTests() => Use("a");

    private void Use(string device) => SavedTvs.Configure(Path.Combine(_dir, device, SavedTvs.FileName));

    public void Dispose()
    {
        SavedTvs.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static TvProfile Profile(params string[] names) => new()
    {
        TvName = "Living room",
        Apps = names.Select(n => new TvProfileApp { Name = n, AppId = n + ".app", PackageFile = n + ".wgt" }).ToList(),
    };

    private byte[] Export()
    {
        using var zip = new MemoryStream();
        Apps2Samsung.Backup.BackupService.Export(zip, "{}", Path.Combine(_dir, "no-certificates"));
        return zip.ToArray();
    }

    private void Import(byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        Apps2Samsung.Backup.BackupService.Import(stream, Path.Combine(_dir, "certificates-in"));
    }

    [Fact]
    public void A_backup_carries_the_saved_tvs_to_another_device()
    {
        SavedTvs.Remember("192.168.1.50", Profile("Jellyfin", "Litefin"), Day1);
        var zip = Export();

        Use("b");
        Assert.Empty(SavedTvs.All());
        Import(zip);

        Assert.Equal(2, SavedTvs.Get("192.168.1.50")!.Profile.Apps.Count);
    }

    [Fact]
    public void A_list_this_device_read_more_recently_is_kept()
    {
        SavedTvs.Remember("192.168.1.50", Profile("Jellyfin"), Day1);
        var zip = Export();

        Use("b");
        SavedTvs.Remember("192.168.1.50", Profile("Jellyfin", "Litefin", "Moonlight"), Day2);
        SavedTvs.Remember("192.168.1.51", Profile("Overscan"), Day2);
        Import(zip);

        Assert.Equal(3, SavedTvs.Get("192.168.1.50")!.Profile.Apps.Count);
        Assert.Equal(2, SavedTvs.All().Count);
    }

    [Fact]
    public void An_older_list_here_is_replaced_by_the_backups()
    {
        SavedTvs.Remember("192.168.1.50", Profile("Jellyfin", "Litefin"), Day2);
        var zip = Export();

        Use("b");
        SavedTvs.Remember("192.168.1.50", Profile("Jellyfin"), Day1);
        Import(zip);

        Assert.Equal(2, SavedTvs.Get("192.168.1.50")!.Profile.Apps.Count);
    }

    [Fact]
    public void A_backup_without_saved_tvs_has_no_entry_and_imports_cleanly()
    {
        var zip = Export();
        using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip)))
            Assert.DoesNotContain(archive.Entries, e => e.FullName == SavedTvs.FileName);

        Import(zip);
        Assert.Empty(SavedTvs.All());
    }

    [Fact]
    public void Text_that_is_not_a_list_brings_in_nothing()
    {
        Assert.Equal(0, SavedTvs.Merge("{ not json"));
        Assert.Equal(0, SavedTvs.Merge("[1,2,3]"));
        Assert.Empty(SavedTvs.All());
    }
}
