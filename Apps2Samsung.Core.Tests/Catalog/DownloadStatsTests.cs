using System.Globalization;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

public class DownloadStatsTests
{
    // The shape tools/DownloadStats writes: the community bundle per file, the others as a total.
    private const string StatsJson = """
        {
          "schemaVersion": 1,
          "generatedAt": "2026-10-27T04:17:00Z",
          "recentSince": "2026-09-27",
          "providers": {
            "tizen-community": {
              "total": 900, "recent": 90,
              "files": {
                "Moonlight-Tizen.wgt": { "total": 500, "recent": 50 },
                "Rooted-A.wgt": { "total": 150, "recent": 10 },
                "Rooted-B.wgt": { "total": 250, "recent": 30 }
              }
            },
            "tizentube": { "total": 700, "recent": 70 }
          }
        }
        """;

    private static GitHubRelease App(string name, params string[] files) => new()
    {
        Name = name,
        Assets = files.Select(f => new Asset { FileName = f }).ToList(),
    };

    private static string Localize(string key) => key;

    [Fact]
    public void Stamp_SumsAGroupedAppsFiles_AndGivesOtherProvidersTheirTotal()
    {
        var stats = DownloadStatsService.Parse(StatsJson);
        var moonlight = App("Moonlight", "Moonlight-Tizen.wgt");
        var rooted = App("Rooted", "Rooted-A.wgt", "Rooted-B.wgt");
        var unknown = App("New app", "New.wgt");
        var tizentube = App("TizenTube", "TizenTube.wgt");

        DownloadStatsService.Stamp(new[] { moonlight, rooted, unknown }, new ProviderEntry { Id = "tizen-community", ExpandAssets = true }, stats);
        DownloadStatsService.Stamp(new[] { tizentube }, new ProviderEntry { Id = "tizentube" }, stats);

        Assert.Equal(500, moonlight.Downloads);
        Assert.Equal(400, rooted.Downloads);
        Assert.Equal(0, unknown.Downloads);
        Assert.Equal(700, tizentube.Downloads);
    }

    [Fact]
    public void Stamp_LeavesEntriesAlone_WithoutStatsOrAMatchingProvider()
    {
        var app = App("Litefin", "Litefin.wgt");

        DownloadStatsService.Stamp(new[] { app }, new ProviderEntry { Id = "litefin" }, null);
        DownloadStatsService.Stamp(new[] { app }, new ProviderEntry { Id = "litefin" }, DownloadStatsService.Parse(StatsJson));

        Assert.Equal(0, app.Downloads);
    }

    [Fact]
    public void Parse_RejectsAnUnknownSchema()
    {
        Assert.Null(DownloadStatsService.Parse("""{ "schemaVersion": 2, "providers": {} }"""));
        Assert.Null(DownloadStatsService.Parse("not json"));
    }

    [Theory]
    [InlineData(842, "842")]
    [InlineData(4_821, "4.8k")]
    [InlineData(29_333, "29k")]
    [InlineData(1_234_567, "1.2M")]
    public void Compact_ShortensLargeCounts(long count, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, DownloadStatsService.Compact(count));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void MostDownloaded_FollowsFavorites_RanksOnce_AndExpandsToEveryCountedApp()
    {
        var apps = new List<GitHubRelease>
        {
            new() { Name = "A", Category = "media", Downloads = 10 },
            new() { Name = "B", Category = "media", Downloads = 300 },
            new() { Name = "C", Category = "games", Downloads = 200 },
            new() { Name = "Jellyfin", Category = "media", Downloads = 900 },
            new() { Name = "Jellyfin", Category = "media", Downloads = 900 },
            new() { Name = "D", Category = "tools" },
        };
        var favorites = new HashSet<string> { "D" };

        var groups = CatalogGroups.Build(apps, null, new HashSet<string>(), Localize, favorites);

        Assert.Equal(CatalogGroups.FavoritesId, groups[0].CategoryId);
        var popular = groups[1];
        Assert.Equal(CatalogGroups.PopularId, popular.CategoryId);
        Assert.Equal(4, popular.Count);
        Assert.Equal(new[] { "Jellyfin", "B", "C" }, popular.Rows.Select(r => r.Name));
        Assert.True(popular.IsTruncated);

        var expanded = CatalogGroups.Build(apps, null, new HashSet<string> { CatalogGroups.PopularId }, Localize);
        Assert.Equal(new[] { "Jellyfin", "B", "C", "A" }, expanded[0].Rows.Select(r => r.Name));
    }

    [Fact]
    public void MostDownloaded_IsLeftOut_WithoutCounts_AndWhileSearching()
    {
        var apps = new List<GitHubRelease> { new() { Name = "A", Category = "media", Downloads = 10 } };

        Assert.DoesNotContain(CatalogGroups.Build(new List<GitHubRelease> { new() { Name = "A" } }, null, new HashSet<string>(), Localize),
            g => g.CategoryId == CatalogGroups.PopularId);
        Assert.DoesNotContain(CatalogGroups.Build(apps, "A", new HashSet<string>(), Localize),
            g => g.CategoryId == CatalogGroups.PopularId);
    }
}
