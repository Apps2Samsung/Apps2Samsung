using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Catalog;

// A TV's apps written down and found again in the catalog (#1069).
public class TvProfilesTests
{
    private const string Ip = "192.168.1.50";
    private const string LitefinId = "AprZAARz4r.Litefin";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<CommunityCatalogRemoved> NothingRemoved = Array.Empty<CommunityCatalogRemoved>();

    private static Asset File(string name, string version, string appId, string? variant = null, UserSettingsSpec? settings = null) => new()
    {
        FileName = name,
        DownloadUrl = $"https://example.test/{name}",
        PackageVersion = version,
        AppId = appId,
        Variant = variant,
        UserSettings = settings,
    };

    private static GitHubRelease Litefin(string version) => new()
    {
        Name = "Litefin",
        TagName = $"v{version}",
        PublishedAt = "2026-11-01T00:00:00Z",
        HasVariants = true,
        Assets = new List<Asset>
        {
            File($"Litefin-{version}-Tizen-Normal.wgt", version, LitefinId, "Normal"),
            File($"Litefin-{version}-Tizen-Legacy.wgt", version, LitefinId, "Legacy"),
        },
    };

    private static GitHubRelease Single(string name, string appId, string version, CatalogSource? source = null, UserSettingsSpec? settings = null) => new()
    {
        Name = name,
        TagName = $"v{version}",
        PublishedAt = "2026-11-01T00:00:00Z",
        Source = source ?? CatalogSource.Official,
        Assets = new List<Asset> { File($"{name}-{version}.wgt", version, appId, settings: settings) },
    };

    private static InstalledApp OnTv(string title, string id, string version) => new(title, null, id, version, "2026-11-01", true);

    private static string Remembered(string variant) => InstalledVariantStore.Set(null, new InstalledVariantRecord
    {
        TvIp = Ip,
        AppId = LitefinId,
        CatalogApp = "Litefin",
        Variant = variant,
        PackageFile = $"Litefin-2.0.30-Tizen-{variant}.wgt",
    });

    private static UserSettingsSpec ServerSpec() => new()
    {
        File = "config.js",
        Fields = new List<UserSettingsField> { new() { Key = "server", Label = "Server", Find = "SERVER", Replace = "{value}" } },
    };

    private static TvProfileSaved Save(IEnumerable<InstalledApp> apps, IReadOnlyList<GitHubRelease> catalog,
        string? variants = null, string? settings = null, bool withSettings = false, IReadOnlyList<CommunityCatalogRemoved>? removed = null) =>
        TvProfiles.Save(apps, catalog, removed ?? NothingRemoved, null, variants, Ip, null, settings, withSettings, Now, "Living room", "QE55");

    [Fact]
    public void Save_KeepsCatalogApps_AndLeavesTheRestOut()
    {
        var catalog = new[] { Single("Moonfin", "abc1234567.Moonfin", "1.2.0") };
        var saved = Save(new[] { OnTv("Moonfin", "abc1234567.Moonfin", "1.1.0"), OnTv("Netflix", "org.tizen.netflix", "9") }, catalog);

        var app = Assert.Single(saved.Profile.Apps);
        Assert.Equal("Moonfin", app.Name);
        Assert.Equal("abc1234567.Moonfin", app.AppId);
        Assert.Equal("Moonfin-1.2.0.wgt", app.PackageFile);
        Assert.Equal("official", app.Source);
        Assert.Equal("1.1.0", app.Version);
        Assert.False(app.Uncertain);
        Assert.Null(app.Settings);
        var left = Assert.Single(saved.LeftOut);
        Assert.Equal(TvProfileLeftOutReason.NotInCatalog, left.Reason);
        Assert.Equal("Living room", saved.Profile.TvName);
        Assert.Equal(Now, saved.Profile.SavedAtUtc);
    }

    [Fact]
    public void Save_TakesTheRememberedVariant()
    {
        var saved = Save(new[] { OnTv("Litefin", LitefinId, "2.0.30") }, new[] { Litefin("2.0.30") }, variants: Remembered("Legacy"));

        var app = Assert.Single(saved.Profile.Apps);
        Assert.Equal("Legacy", app.Variant);
        Assert.False(app.Uncertain);
    }

    [Fact]
    public void Save_WithoutARecord_SaysTheVariantIsAGuess()
    {
        var saved = Save(new[] { OnTv("Litefin", LitefinId, "2.0.30") }, new[] { Litefin("2.0.30") });

        Assert.True(Assert.Single(saved.Profile.Apps).Uncertain);
    }

    [Fact]
    public void Save_IncludesValues_OnlyWhenAsked()
    {
        var spec = ServerSpec();
        var catalog = new[] { Single("Tessel", "tes1234567.Tessel", "1.0.0", settings: spec) };
        var apps = new[] { OnTv("Tessel", "tes1234567.Tessel", "1.0.0") };
        var stored = UserSettingsStore.Set(null, "Tessel-1.0.0.wgt", spec, new Dictionary<string, string> { ["server"] = "http://nas:8096" });

        var without = Save(apps, catalog, settings: stored);
        Assert.Null(Assert.Single(without.Profile.Apps).Settings);
        Assert.False(without.Profile.ContainsSettings);
        Assert.DoesNotContain("nas", TvProfiles.Serialize(without.Profile));

        var with = Save(apps, catalog, settings: stored, withSettings: true);
        Assert.Equal("http://nas:8096", Assert.Single(with.Profile.Apps).Settings!["server"]);
        Assert.True(with.Profile.ContainsSettings);
    }

    [Fact]
    public void RoundTrips_AndPlansEveryAppOnAnEmptyTv()
    {
        var catalog = new[] { Litefin("2.0.30"), Single("Moonfin", "abc1234567.Moonfin", "1.2.0") };
        var saved = Save(new[] { OnTv("Litefin", LitefinId, "2.0.30"), OnTv("Moonfin", "abc1234567.Moonfin", "1.2.0") }, catalog, variants: Remembered("Legacy"));

        var profile = TvProfiles.Parse(TvProfiles.Serialize(saved.Profile));
        // The catalog moved on a release since.
        var plan = TvProfiles.Plan(profile, new[] { Litefin("2.0.31"), Single("Moonfin", "abc1234567.Moonfin", "1.3.0") }, Array.Empty<InstalledApp>());

        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.Equal(TvRestoreState.Ready, p.State));
        var litefin = plan.Single(p => p.App.Name == "Litefin");
        Assert.Equal("Litefin-2.0.31-Tizen-Legacy.wgt", litefin.Asset!.FileName);
        Assert.False(litefin.VariantChanged);
        Assert.Equal("Moonfin-1.3.0.wgt", plan.Single(p => p.App.Name == "Moonfin").Asset!.FileName);
    }

    [Fact]
    public void Plan_SkipsWhatTheTvHas_AndSaysWhatTheCatalogLost()
    {
        var profile = new TvProfile
        {
            Apps =
            {
                new TvProfileApp { Name = "Moonfin", AppId = "abc1234567.Moonfin", PackageFile = "Moonfin-1.2.0.wgt" },
                new TvProfileApp { Name = "Gone", AppId = "gon1234567.Gone", PackageFile = "Gone-1.0.wgt" },
            },
        };
        var plan = TvProfiles.Plan(profile, new[] { Single("Moonfin", "abc1234567.Moonfin", "1.3.0") },
            new[] { OnTv("Moonfin", "abc1234567.Moonfin", "1.2.0") });

        Assert.Equal(TvRestoreState.AlreadyOnTv, plan.Single(p => p.App.Name == "Moonfin").State);
        Assert.Equal(TvRestoreState.NotInCatalog, plan.Single(p => p.App.Name == "Gone").State);
    }

    [Fact]
    public void Plan_ADroppedVariant_FallsBackToTheAppsFile_AndSaysSo()
    {
        var profile = new TvProfile
        {
            Apps = { new TvProfileApp { Name = "Litefin", AppId = LitefinId, PackageFile = "Litefin-2.0.30-Tizen-Ultra.wgt", Variant = "Ultra" } },
        };
        var item = Assert.Single(TvProfiles.Plan(profile, new[] { Litefin("2.0.31") }, Array.Empty<InstalledApp>()));

        Assert.Equal(TvRestoreState.Ready, item.State);
        Assert.Equal("Litefin", item.Release!.Name);
        Assert.True(item.VariantChanged);
    }

    [Fact]
    public void Plan_InstallsOnlyFromTheSourceItCameFrom()
    {
        var repo = new CatalogSource(CatalogSourceKind.Repository, "Example", "example.test");
        var profile = new TvProfile
        {
            Apps = { new TvProfileApp { Name = "Moonfin", AppId = "abc1234567.Moonfin", PackageFile = "Moonfin-1.2.0.wgt", Source = "example.test" } },
        };

        // The repository is not added on this machine: the official entry with the same id does not stand in for it.
        var withoutRepo = TvProfiles.Plan(profile, new[] { Single("Moonfin", "abc1234567.Moonfin", "1.3.0") }, Array.Empty<InstalledApp>());
        Assert.Equal(TvRestoreState.NotInCatalog, Assert.Single(withoutRepo).State);

        var withRepo = TvProfiles.Plan(profile, new[]
        {
            Single("Moonfin", "abc1234567.Moonfin", "1.3.0"),
            Single("Moonfin", "abc1234567.Moonfin", "1.4.0", repo),
        }, Array.Empty<InstalledApp>());
        var item = Assert.Single(withRepo);
        Assert.Equal(TvRestoreState.Ready, item.State);
        Assert.Equal("example.test", item.Release!.Source.Key);
    }

    [Fact]
    public void Plan_ForksSharingAnId_RestoreTheAppThatWasThere()
    {
        const string shared = "AprZAARz4r.Jellyfin";
        var catalog = new[] { Single("Jellyfin", shared, "10.10.0"), Single("Tacplayer", shared, "1.0.0") };
        var profile = new TvProfile
        {
            Apps = { new TvProfileApp { Name = "Tacplayer", AppId = shared, PackageFile = "Tacplayer-0.9.0.wgt" } },
        };

        var item = Assert.Single(TvProfiles.Plan(profile, catalog, Array.Empty<InstalledApp>()));
        Assert.Equal("Tacplayer", item.Release!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    public void Parse_RefusesWhatIsNotAProfile(string text) =>
        Assert.Throws<FormatException>(() => TvProfiles.Parse(text));

    [Fact]
    public void Parse_RefusesANewerFormat()
    {
        var ex = Assert.Throws<FormatException>(() => TvProfiles.Parse("{\"schema\": 2, \"apps\": []}"));
        Assert.Contains("newer", ex.Message);
    }
}
