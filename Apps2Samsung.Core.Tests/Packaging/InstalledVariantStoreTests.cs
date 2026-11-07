using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

public class InstalledVariantStoreTests
{
    private static InstalledVariantRecord Record(string ip, string appId, string file, string? variant = null, string? duid = null, string? app = "Litefin") => new()
    {
        TvIp = ip,
        TvDuid = duid,
        AppId = appId,
        CatalogApp = app,
        Variant = variant,
        PackageFile = file,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public void Read_NothingUsable_IsEmpty(string? json)
    {
        Assert.Empty(InstalledVariantStore.Read(json));
    }

    [Fact]
    public void Set_ReplacesTheRecordForTheSameTvAndApp()
    {
        var json = InstalledVariantStore.Set(null, Record("192.168.1.8", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt", "Normal"));
        json = InstalledVariantStore.Set(json, Record("192.168.1.8", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Legacy.wgt", "Legacy"));
        json = InstalledVariantStore.Set(json, Record("192.168.1.9", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt", "Normal"));

        var records = InstalledVariantStore.Read(json);
        Assert.Equal(2, records.Count);
        Assert.Equal("Legacy", InstalledVariantStore.Find(json, "192.168.1.8", "Pkg.Litefin")!.Variant);
        Assert.Equal("Normal", InstalledVariantStore.Find(json, "192.168.1.9", "Pkg.Litefin")!.Variant);
    }

    [Fact]
    public void Set_WithoutAppIdOrFile_KeepsNothing()
    {
        Assert.Equal(string.Empty, InstalledVariantStore.Set(null, Record("192.168.1.8", "", "Litefin.wgt")));
        Assert.Equal(string.Empty, InstalledVariantStore.Set(null, Record("192.168.1.8", "Pkg.Litefin", "")));
    }

    [Fact]
    public void Find_PrefersTheDuidOverTheAddress()
    {
        var json = InstalledVariantStore.Set(null, Record("192.168.1.8", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt", "Normal", duid: "DUID-A"));
        json = InstalledVariantStore.Set(json, Record("192.168.1.9", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Legacy.wgt", "Legacy", duid: "DUID-B"));

        // The TVs swapped addresses: the DUID says which is which.
        Assert.Equal("Legacy", InstalledVariantStore.Find(json, "192.168.1.8", "Pkg.Litefin", "DUID-B")!.Variant);
        Assert.Equal("Normal", InstalledVariantStore.Find(json, "192.168.1.9", "Pkg.Litefin", "DUID-A")!.Variant);
    }

    [Fact]
    public void Find_UnknownTv_TakesTheOneVariantEveryTvAgreesOn()
    {
        var json = InstalledVariantStore.Set(null, Record("192.168.1.8", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt", "Normal"));
        json = InstalledVariantStore.Set(json, Record("192.168.1.9", "Pkg.Litefin", "Litefin-2.0.29-Tizen-Normal.wgt", "Normal"));

        // A TV that changed address still gets its variant...
        Assert.Equal("Normal", InstalledVariantStore.Find(json, "192.168.1.50", "Pkg.Litefin")!.Variant);

        // ...unless the TVs disagree, when guessing would be wrong half the time.
        json = InstalledVariantStore.Set(json, Record("192.168.1.9", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Legacy.wgt", "Legacy"));
        Assert.Null(InstalledVariantStore.Find(json, "192.168.1.50", "Pkg.Litefin"));
        Assert.Null(InstalledVariantStore.Find(json, "192.168.1.8", "Other.App"));
    }

    [Theory]
    [InlineData("Litefin-2.0.30-Tizen-Normal.wgt", "litefin-tizen-normal.wgt")]
    [InlineData("Litefin-2.0.31-Tizen-Normal.wgt", "litefin-tizen-normal.wgt")]
    [InlineData("Litefin-2.0.30-Tizen-Ultra-Legacy-NoService.wgt", "litefin-tizen-ultra-legacy-noservice.wgt")]
    [InlineData("Jellyfin.wgt", "jellyfin.wgt")]
    [InlineData("Moonlight_v1.2.3-beta.4_secondary.wgt", "moonlight_secondary.wgt")]
    [InlineData("TizenTube-Cobalt-5.0.tpk", "tizentube-cobalt.tpk")]
    [InlineData("", "")]
    public void Family_TakesTheVersionOut(string file, string family)
    {
        Assert.Equal(family, InstalledVariantStore.Family(file));
    }

    [Fact]
    public void IsSameVariant_ByLabelWhenBothHaveOne_ElseByFileFamily()
    {
        var labelled = Record("ip", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt", "Normal");
        Assert.True(InstalledVariantStore.IsSameVariant(labelled, "Litefin-2.0.31-Tizen-Normal.wgt", "Normal"));
        Assert.False(InstalledVariantStore.IsSameVariant(labelled, "Litefin-2.0.31-Tizen-Legacy.wgt", "Legacy"));
        // A catalog that lost its labels: the file name still tells.
        Assert.True(InstalledVariantStore.IsSameVariant(labelled, "Litefin-2.0.31-Tizen-Normal.wgt", null));

        var unlabelled = Record("ip", "Pkg.Litefin", "Litefin-2.0.30-Tizen-Normal.wgt");
        Assert.True(InstalledVariantStore.IsSameVariant(unlabelled, "Litefin-2.0.31-Tizen-Normal.wgt", "Normal"));
        Assert.False(InstalledVariantStore.IsSameVariant(unlabelled, "Litefin-2.0.31-Tizen-Legacy.wgt", "Legacy"));
        Assert.False(InstalledVariantStore.IsSameVariant(unlabelled, null, null));
    }
}
