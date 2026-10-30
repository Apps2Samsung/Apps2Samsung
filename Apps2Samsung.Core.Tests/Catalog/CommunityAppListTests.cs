using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

public class CommunityAppListTests
{
    // The shape tizen-community-packages' build-catalog.sh emits for Vetro, a legacy single-file
    // package, a mixed block this version can't write, and a root group.
    private const string CatalogJson = """
        {
          "schemaVersion": 1,
          "apps": [
            { "file": "Vetro.wgt", "name": "Vetro", "category": "tools", "repo": "andrearicci93/vetro", "flags": ["root"], "required_version": "6.0",
              "user_settings": { "fields": [ { "key": "port", "label": "Port", "type": "number", "required": false, "default": "8099",
                "min": 1024, "max": 65535, "disallow": [8001, 8002, 26101],
                "targets": [ { "file": "config.xml", "find": "8099", "replace": "{value}" } ] } ] } },
            { "file": "Frame-Dashboard.wgt", "name": "Frame Dashboard", "category": "other", "repo": "a/b",
              "user_settings": { "file": "dashboard-url.js", "fields": [ { "key": "dashboard_url", "label": "URL", "type": "text", "find": "x", "replace": "{value}" } ] } },
            { "file": "Mixed.wgt", "name": "Mixed", "category": "other", "repo": "a/c",
              "user_settings": { "file": "a.js", "fields": [ { "key": "k", "label": "K", "type": "text", "targets": [ { "file": "a.js", "find": "x", "replace": "{value}" } ] } ] } },
            { "file": "Rooted-A.wgt", "name": "Rooted", "group": "Rooted", "category": "tools", "repo": "a/d" },
            { "file": "Rooted-B.wgt", "name": "Rooted", "group": "Rooted", "category": "tools", "repo": "a/d", "flags": ["root"] }
          ]
        }
        """;

    private static List<GitHubRelease> Expand()
    {
        var catalog = JsonSerializer.Deserialize<CommunityCatalog>(CatalogJson, JsonSerializerOptionsProvider.Default)!;
        var bundle = new GitHubRelease
        {
            Name = "community",
            TagName = "community-1",
            Assets = catalog.Apps.Select(a => new Asset { FileName = a.File }).ToList(),
        };
        return CommunityAppList.Expand(new[] { bundle }, catalog, requiresPartner: false);
    }

    private static GitHubRelease Named(List<GitHubRelease> list, string name) => list.Single(r => r.Name == name);

    [Fact]
    public void RequiredVersion_IsStampedOnTheAsset_AndNullWhenTheCatalogHasNone()
    {
        var list = Expand();

        Assert.Equal("6.0", Named(list, "Vetro").Assets[0].RequiredVersion);
        Assert.Null(Named(list, "Frame Dashboard").Assets[0].RequiredVersion);
    }

    [Fact]
    public void RootFlag_IsStamped_AndAGroupIsRootWhenAnyFileIs()
    {
        var list = Expand();

        Assert.True(Named(list, "Vetro").IsRoot);
        Assert.False(Named(list, "Frame Dashboard").IsRoot);
        Assert.True(Named(list, "Rooted").IsRoot);
    }

    [Fact]
    public void Visible_HidesRootEntries_OnlyWhenAskedTo()
    {
        var list = Expand();

        Assert.Contains(CommunityAppList.Visible(list, showAdult: false), r => r.Name == "Vetro");
        Assert.DoesNotContain(CommunityAppList.Visible(list, showAdult: false, showRoot: false), r => r.IsRoot);
        Assert.Contains(CommunityAppList.Visible(list, showAdult: false, showRoot: false), r => r.Name == "Frame Dashboard");
    }

    [Fact]
    public void UserSettings_TargetsAndSingleFile_AreKept_MixedIsDropped()
    {
        var list = Expand();

        var port = Assert.Single(Named(list, "Vetro").Assets[0].UserSettings!.Fields);
        Assert.Equal("8099", port.Default);
        Assert.Equal(1024, port.Min);
        Assert.Equal(65535, port.Max);
        Assert.Equal(new long[] { 8001, 8002, 26101 }, port.Disallow);
        Assert.Equal("config.xml", Assert.Single(port.Targets!).File);

        Assert.Equal("dashboard-url.js", Named(list, "Frame Dashboard").Assets[0].UserSettings!.File);
        Assert.Null(Named(list, "Mixed").Assets[0].UserSettings);
    }
}
