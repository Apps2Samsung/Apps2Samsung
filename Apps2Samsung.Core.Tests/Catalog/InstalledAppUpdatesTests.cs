using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Catalog;

// An app with several variants (Litefin) ships them all under one application id and one
// version, so the TV cannot say which it runs. The installed-variant store (#975) can.
public class InstalledAppUpdatesTests
{
    private const string AppId = "AprZAARz4r.Litefin";

    private static Asset File(string name, string version, string? variant) => new()
    {
        FileName = name,
        DownloadUrl = $"https://example.test/{name}",
        PackageVersion = version,
        AppId = AppId,
        Variant = variant,
    };

    private static GitHubRelease Litefin(string version) => new()
    {
        Name = "Litefin",
        TagName = $"v{version}",
        PublishedAt = "2026-11-01T00:00:00Z",
        HasVariants = true,
        Assets = new List<Asset>
        {
            File($"Litefin-{version}-Tizen-Normal.wgt", version, "Normal"),
            File($"Litefin-{version}-Tizen-Legacy.wgt", version, "Legacy"),
            File($"Litefin-{version}-Tizen-Modern.wgt", version, "Modern"),
        },
    };

    private static InstalledApp OnTv(string version) => new("Litefin", null, AppId, version, "2026-11-01", true);

    private static string Remembered(string ip, string variant, string version = "2.0.30") =>
        InstalledVariantStore.Set(null, new InstalledVariantRecord
        {
            TvIp = ip,
            AppId = AppId,
            CatalogApp = "Litefin",
            Variant = variant,
            PackageFile = $"Litefin-{version}-Tizen-{variant}.wgt",
        });

    [Fact]
    public void WithoutARecord_SameVersion_IsUpToDate_OnWhicheverFile()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.30") });

        Assert.NotNull(match);
        Assert.Equal(CatalogVersionState.UpToDate, match!.State);
        Assert.False(match.VariantRemembered);
    }

    [Fact]
    public void WithoutARecord_NewerCatalog_NamesTheFirstFile()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.31") });

        Assert.Equal(CatalogVersionState.UpdateAvailable, match!.State);
        Assert.Equal("Litefin-2.0.31-Tizen-Normal.wgt", match.Asset.FileName);
        Assert.False(match.VariantRemembered);
    }

    [Fact]
    public void WithARecord_TheUpdateIsTheSameVariant()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.31") }, null, Remembered("192.168.1.8", "Legacy"), "192.168.1.8");

        Assert.Equal(CatalogVersionState.UpdateAvailable, match!.State);
        Assert.Equal("Litefin-2.0.31-Tizen-Legacy.wgt", match.Asset.FileName);
        Assert.Equal("Legacy", match.Asset.Variant);
        Assert.True(match.VariantRemembered);
    }

    [Fact]
    public void WithARecord_SameVersion_IsUpToDate_OnThatVariant()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.30") }, null, Remembered("192.168.1.8", "Modern"), "192.168.1.8");

        Assert.Equal(CatalogVersionState.UpToDate, match!.State);
        Assert.Equal("Modern", match.Asset.Variant);
        Assert.True(match.VariantRemembered);
    }

    [Fact]
    public void ARecordForAnotherTv_DoesNotApply()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.31") }, null, Remembered("192.168.1.9", "Legacy"), "192.168.1.8");

        // One TV in the store, so its variant is the best guess for an unknown address (#975);
        // the flag still says it was remembered, not read off this TV.
        Assert.Equal("Legacy", match!.Asset.Variant);
        Assert.True(match.VariantRemembered);

        var two = InstalledVariantStore.Set(Remembered("192.168.1.9", "Legacy"), new InstalledVariantRecord
        {
            TvIp = "192.168.1.10", AppId = AppId, CatalogApp = "Litefin", Variant = "Modern", PackageFile = "Litefin-2.0.30-Tizen-Modern.wgt",
        });
        match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.31") }, null, two, "192.168.1.8");
        Assert.Equal("Normal", match!.Asset.Variant);
        Assert.False(match.VariantRemembered);
    }

    [Fact]
    public void ARecordedVariantTheCatalogDropped_FallsBackToTheCatalogsPick()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.31") }, null, Remembered("192.168.1.8", "Ultra"), "192.168.1.8");

        Assert.Equal(CatalogVersionState.UpdateAvailable, match!.State);
        Assert.Equal("Normal", match.Asset.Variant);
        Assert.False(match.VariantRemembered);
    }

    [Fact]
    public void ARecordOfARenamedApp_MatchesByTheOriginalId()
    {
        // Installed under a new package id (#839): the TV lists New.Litefin, the record and the
        // catalog both carry the id the file was built with.
        var renames = PackageIdRenameStore.Set(null, new PackageIdRenameRecord
        {
            TvDuid = "DUID", OriginalPackageId = "AprZAARz4r", NewPackageId = "New", OriginalAppId = AppId, NewAppId = "New.Litefin",
        });
        var onTv = new InstalledApp("Litefin", null, "New.Litefin", "2.0.30", "2026-11-01", true);

        var match = InstalledAppUpdates.Find(onTv, new[] { Litefin("2.0.31") }, renames, Remembered("192.168.1.8", "Legacy"), "192.168.1.8");

        Assert.Equal("Legacy", match!.Asset.Variant);
        Assert.True(match.VariantRemembered);
    }

    // ---- updates only from the source the app came from (#1018) ----

    private static GitHubRelease FromRepository(string version, string repositoryId = "io.github.someone.tizen")
    {
        var release = Litefin(version);
        release.Source = new CatalogSource(CatalogSourceKind.Repository, "Someone's apps", repositoryId);
        return release;
    }

    private static string RememberedFrom(string source) =>
        InstalledVariantStore.Set(null, new InstalledVariantRecord
        {
            TvIp = "10.0.0.5",
            AppId = AppId,
            CatalogApp = "Litefin",
            Variant = "Normal",
            PackageFile = "Litefin-2.0.30-Tizen-Normal.wgt",
            Source = source,
        });

    [Fact]
    public void A_repository_never_offers_an_update_for_an_app_installed_from_elsewhere()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.30"), FromRepository("9.9.9") });

        Assert.NotNull(match);
        Assert.Equal(CatalogSourceKind.Official, match!.Release.Source.Kind);
        Assert.Equal(CatalogVersionState.UpToDate, match.State);
    }

    [Fact]
    public void Without_a_record_an_app_only_a_repository_offers_is_not_matched()
    {
        Assert.Null(InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { FromRepository("2.1.0") }));
    }

    [Fact]
    public void An_app_installed_from_a_repository_updates_from_that_repository_only()
    {
        var catalog = new[] { Litefin("3.0.0"), FromRepository("2.1.0"), FromRepository("5.0.0", "org.example.other") };

        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), catalog, installedVariantsJson: RememberedFrom("io.github.someone.tizen"), tvIp: "10.0.0.5");

        Assert.NotNull(match);
        Assert.Equal("io.github.someone.tizen", match!.Release.Source.RepositoryId);
        Assert.Equal("2.1.0", match.CatalogVersion);
        Assert.Equal(CatalogVersionState.UpdateAvailable, match.State);
    }

    [Fact]
    public void An_app_installed_from_the_official_catalog_ignores_a_newer_repository_copy()
    {
        var match = InstalledAppUpdates.Find(OnTv("2.0.30"), new[] { Litefin("2.0.30"), FromRepository("2.1.0") },
            installedVariantsJson: RememberedFrom("official"), tvIp: "10.0.0.5");

        Assert.Equal(CatalogVersionState.UpToDate, match!.State);
        Assert.Equal(CatalogSourceKind.Official, match.Release.Source.Kind);
    }
}

// Every Jellyfin build declares 0.1.0, so an installed Jellyfin is compared on the jellyfin-web
// version the catalog reads out of the file and the head wrote down at install (#1113).
public class InstalledJellyfinUpdatesTests
{
    private const string AppId = "AprZAARz4r.Jellyfin";
    private const string Tv = "192.168.1.8";

    private static Asset Build(string name, string webVersion) => new()
    {
        FileName = name,
        DownloadUrl = $"https://example.test/{name}",
        PackageVersion = "0.1.0",
        AppId = AppId,
        WebVersion = webVersion,
    };

    private static GitHubRelease[] Catalog(string release, string branch, string master = "13.0.0") => new[]
    {
        new GitHubRelease
        {
            Name = "Jellyfin",
            TagName = "2026-11-15-1045",
            PublishedAt = "2026-11-15T10:54:41Z",
            Assets = new List<Asset>
            {
                Build("Jellyfin.wgt", release),
                Build("Jellyfin-12.z.wgt", branch),
                Build("Jellyfin-master.wgt", master),
            },
        },
    };

    private static InstalledApp OnTv() => new("Jellyfin", null, AppId, "0.1.0", "2026-10-23", true);

    private static string Installed(string file, string? webVersion, string ip = Tv) =>
        InstalledVariantStore.Set(null, new InstalledVariantRecord
        {
            TvIp = ip,
            AppId = AppId,
            CatalogApp = "Jellyfin",
            PackageFile = file,
            WebVersion = webVersion,
        });

    [Fact]
    public void ANewerWebVersion_IsAnUpdate_OfTheFileTheTvHas()
    {
        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.5.0", "12.5.0"), null, Installed("Jellyfin-12.z.wgt", "12.4.0"), Tv);

        Assert.NotNull(match);
        Assert.Equal(CatalogVersionState.UpdateAvailable, match!.State);
        Assert.True(match.HasUpdate);
        Assert.Equal("Jellyfin-12.z.wgt", match.Asset.FileName);
        Assert.Equal("12.5.0", match.CatalogVersion);
        Assert.Equal("12.4.0", match.InstalledVersion);
        Assert.False(match.InstalledVersionUnknown);
    }

    [Fact]
    public void TheSameWebVersion_IsUpToDate_HoweverOftenItWasRebuilt()
    {
        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.4.0", "12.4.0"), null, Installed("Jellyfin.wgt", "12.4.0"), Tv);

        Assert.Equal(CatalogVersionState.UpToDate, match!.State);
        Assert.False(match.HasUpdate);
    }

    [Fact]
    public void Master_StaysUpToDate_UntilItsVersionIsRaised()
    {
        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.5.0", "12.5.0"), null, Installed("Jellyfin-master.wgt", "13.0.0"), Tv);

        Assert.Equal("Jellyfin-master.wgt", match!.Asset.FileName);
        Assert.Equal(CatalogVersionState.UpToDate, match.State);
    }

    [Fact]
    public void WithoutARecord_TheInstalledVersionIsNotKnown_AndNothingIsOffered()
    {
        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.5.0", "12.5.0"), null, null, Tv);

        Assert.NotNull(match);
        Assert.Equal(CatalogVersionState.Unknown, match!.State);
        Assert.True(match.InstalledVersionUnknown);
        Assert.False(match.HasUpdate);
        Assert.Null(match.InstalledVersion);
    }

    [Fact]
    public void ARecordFromBeforeTheVersionWasKept_ReadsAsNotKnown()
    {
        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.5.0", "12.5.0"), null, Installed("Jellyfin.wgt", null), Tv);

        Assert.Equal(CatalogVersionState.Unknown, match!.State);
        Assert.True(match.InstalledVersionUnknown);
        Assert.Equal("Jellyfin.wgt", match.Asset.FileName);
        Assert.Equal("12.5.0", match.CatalogVersion);
    }

    [Fact]
    public void RecordsOfTwoOtherTvsThatDisagree_SayNothingAboutThisOne()
    {
        // One record under another address is taken for this TV's (it changed address), as the
        // store does for the variant; two that name different files are not.
        var records = InstalledVariantStore.Set(Installed("Jellyfin.wgt", "12.4.0", "192.168.1.9"), new InstalledVariantRecord
        {
            TvIp = "192.168.1.10", TvDuid = "OTHER", AppId = AppId, CatalogApp = "Jellyfin", PackageFile = "Jellyfin-12.z.wgt", WebVersion = "12.4.0",
        });

        var match = InstalledAppUpdates.Find(OnTv(), Catalog("12.5.0", "12.5.0"), null, records, Tv);

        Assert.Equal(CatalogVersionState.Unknown, match!.State);
        Assert.True(match.InstalledVersionUnknown);
    }

    [Fact]
    public void ACatalogWithoutWebVersions_ComparesOnTheManifestAsBefore()
    {
        var catalog = Catalog("12.5.0", "12.5.0");
        foreach (var asset in catalog[0].Assets)
            asset.WebVersion = null;

        var match = InstalledAppUpdates.Find(OnTv(), catalog, null, Installed("Jellyfin.wgt", "12.4.0"), Tv);

        Assert.Equal(CatalogVersionState.UpToDate, match!.State);
        Assert.False(match.InstalledVersionUnknown);
        Assert.Equal("0.1.0", match.CatalogVersion);
    }

    [Fact]
    public void TheRecordKeepsTheWebVersionAndTheConfiguredMark()
    {
        var json = InstalledVariantStore.Set(null, new InstalledVariantRecord
        {
            TvIp = Tv, AppId = AppId, PackageFile = "Jellyfin.wgt", WebVersion = "12.4.0", Configured = true,
        });

        var record = InstalledVariantStore.Find(json, Tv, AppId);

        Assert.Equal("12.4.0", record!.WebVersion);
        Assert.True(record.Configured);
        // A record of another app carries neither field.
        Assert.DoesNotContain("webVersion", InstalledVariantStore.Set(null, new InstalledVariantRecord { TvIp = Tv, AppId = "x.y", PackageFile = "x.wgt" }));
    }
}
