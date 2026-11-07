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
}
