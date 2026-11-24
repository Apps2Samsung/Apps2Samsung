using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Catalog;

// The debug console rides the web inspector, which a native app (.tpk) does not have. The TV's app
// list does not say what an app was packaged as, so the heads go by what they installed and by
// the catalog.
public class NativeAppsTests
{
    private const string AppId = "StrOrigTV1.StremioOrigin";
    private const string Tv = "192.168.1.8";

    private static GitHubRelease Release(params string[] files) => new()
    {
        Name = "Stremio Origin",
        TagName = "v1.1.1",
        PublishedAt = "2026-11-18T00:00:00Z",
        Assets = files.Select(name => new Asset
        {
            FileName = name,
            DownloadUrl = $"https://example.test/{name}",
            PackageVersion = "1.1.1",
            AppId = AppId,
        }).ToList(),
    };

    private static InstalledApp OnTv(string tizenId = AppId) => new("Stremio Origin", null, tizenId, "1.1.1", "2026-11-18", true);

    private static string Installed(string file) =>
        InstalledVariantStore.Set(null, new InstalledVariantRecord { TvIp = Tv, AppId = AppId, PackageFile = file });

    [Theory]
    [InlineData("StremioOrigin.tpk", true)]
    [InlineData("TizenTube-Cobalt-5.0.TPK", true)]
    [InlineData("Jellyfin.wgt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsNativePackage_GoesByTheExtension(string? file, bool native) =>
        Assert.Equal(native, NativeApps.IsNativePackage(file));

    [Fact]
    public void TheFileThisTvGot_Decides()
    {
        Assert.True(NativeApps.IsKnownNative(OnTv(), null, null, Installed("StremioOrigin.tpk"), Tv));

        // The record wins over the catalog: this TV got the web build of an id that also has a native one.
        Assert.False(NativeApps.IsKnownNative(OnTv(), new[] { Release("App.tpk") }, null, Installed("App.wgt"), Tv));
    }

    [Fact]
    public void WithoutARecord_TheCatalogDecides_OnlyWhenEveryFileIsNative()
    {
        Assert.True(NativeApps.IsKnownNative(OnTv(), new[] { Release("StremioOrigin.tpk") }));
        Assert.False(NativeApps.IsKnownNative(OnTv(), new[] { Release("App.tpk", "App.wgt") }));
        Assert.False(NativeApps.IsKnownNative(OnTv(), new[] { Release("App.wgt") }));
    }

    [Fact]
    public void AnAppNothingIsKnownAbout_IsNotCalledNative()
    {
        Assert.False(NativeApps.IsKnownNative(OnTv("Other.App"), new[] { Release("StremioOrigin.tpk") }));
        Assert.False(NativeApps.IsKnownNative(OnTv(), null));
        Assert.False(NativeApps.IsKnownNative(null, new[] { Release("StremioOrigin.tpk") }));
    }
}
