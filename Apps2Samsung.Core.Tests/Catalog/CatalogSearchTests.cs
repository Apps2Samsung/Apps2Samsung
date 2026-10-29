using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.Catalog;

public class CatalogSearchTests
{
    private static string Localize(string key) => key;

    private static readonly List<GitHubRelease> Apps = new()
    {
        new() { Name = "Moonlight", Author = "OneLiberty" },
        new() { Name = "TizenTube", Author = "reisxd" },
        new() { Name = "Jellyfin" },
    };

    [Fact]
    public void Search_matches_the_app_name()
    {
        var hits = CatalogGroups.Build(Apps, "moon", new HashSet<string>(), Localize)[0].Rows;
        Assert.Equal(new[] { "Moonlight" }, hits.Select(r => r.Name));
    }

    [Fact]
    public void Search_matches_the_developer_name()
    {
        var hits = CatalogGroups.Build(Apps, "reisxd", new HashSet<string>(), Localize)[0].Rows;
        Assert.Equal(new[] { "TizenTube" }, hits.Select(r => r.Name));
    }

    [Fact]
    public void Search_ignores_an_app_without_a_developer()
    {
        var hits = CatalogGroups.Build(Apps, "nobody", new HashSet<string>(), Localize)[0].Rows;
        Assert.Empty(hits);
    }

    [Fact]
    public void TvStore_is_found_by_its_package_id()
    {
        var catalog = new List<GitHubRelease>
        {
            new() { Name = "Other", Assets = { new Asset { FileName = "Other.wgt", PackageId = "OtherPkg01" } } },
            new() { Name = "TV-Store", Assets = { new Asset { FileName = "TV-Store.wgt", PackageId = TvStorePackage.PackageId } } },
        };
        Assert.Equal("TV-Store", TvStorePackage.FindInCatalog(catalog)?.Name);
        Assert.Null(TvStorePackage.FindInCatalog(catalog.Take(1)));
    }
}
