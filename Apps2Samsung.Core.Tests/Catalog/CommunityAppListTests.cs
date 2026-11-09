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
            { "file": "Rooted-B.wgt", "name": "Rooted", "group": "Rooted", "category": "tools", "repo": "a/d", "flags": ["root"] },
            { "file": "yt-5.5.wgt", "name": "YouTube for Tizen", "group": "YouTube for Tizen", "category": "streaming", "repo": "SushyDev/tizen-youtube", "cert_level": "partner" },
            { "file": "yt-5.0.wgt", "name": "YouTube for Tizen", "group": "YouTube for Tizen", "category": "streaming", "repo": "SushyDev/tizen-youtube", "cert_level": "partner" },
            { "file": "Public-Only.wgt", "name": "Public Only", "category": "other", "repo": "a/e", "cert_level": "public" }
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
    public void CertLevel_Partner_IsStamped_AndAGroupNeedsPartnerWhenAnyFileDoes()
    {
        var list = Expand();

        Assert.True(Named(list, "YouTube for Tizen").RequiresPartner);
        Assert.False(Named(list, "Public Only").RequiresPartner);
        Assert.False(Named(list, "Vetro").RequiresPartner);
    }

    [Fact]
    public void CertLevel_RoundTripsThroughTheSnapshot()
    {
        var catalog = JsonSerializer.Deserialize<CommunityCatalog>(CatalogJson, JsonSerializerOptionsProvider.Default)!;
        var json = JsonSerializer.Serialize(catalog, JsonSerializerOptionsProvider.Default);
        var again = JsonSerializer.Deserialize<CommunityCatalog>(json, JsonSerializerOptionsProvider.Default)!;

        Assert.Equal("partner", again.Apps.Single(a => a.File == "yt-5.5.wgt").CertLevel);
        Assert.Null(again.Apps.Single(a => a.File == "Vetro.wgt").CertLevel);
        Assert.DoesNotContain("\"cert_level\":null", json);
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

    // #1016: closed-source and self-updating, alone, together with root, and in groups that are
    // mixed (one open, one closed file) or closed throughout.
    private const string FlagsCatalogJson = """
        {
          "schemaVersion": 1,
          "apps": [
            { "file": "APL-Launcher.wgt", "name": "APL Launcher", "category": "tools", "repo": "a/apl", "flags": ["self-updating"] },
            { "file": "Reelo.wgt", "name": "Reelo", "category": "streaming", "repo": "a/reelo", "flags": ["Closed-Source"] },
            { "file": "Vetro.wgt", "name": "Vetro", "category": "tools", "repo": "a/vetro", "flags": ["root", "self-updating", "closed-source"] },
            { "file": "Plain.wgt", "name": "Plain", "category": "other", "repo": "a/plain" },
            { "file": "Mixed-Open.wgt", "name": "Mixed", "group": "Mixed", "variant": "Open", "category": "other", "repo": "a/mixed" },
            { "file": "Mixed-Closed.wgt", "name": "Mixed", "group": "Mixed", "variant": "Closed", "category": "other", "repo": "a/mixed", "flags": ["closed-source", "self-updating"] },
            { "file": "Shut-A.wgt", "name": "Shut", "group": "Shut", "variant": "A", "category": "other", "repo": "a/shut", "flags": ["closed-source"] },
            { "file": "Shut-B.wgt", "name": "Shut", "group": "Shut", "variant": "B", "category": "other", "repo": "a/shut", "flags": ["closed-source"] }
          ]
        }
        """;

    private static List<GitHubRelease> ExpandFlags()
    {
        var catalog = JsonSerializer.Deserialize<CommunityCatalog>(FlagsCatalogJson, JsonSerializerOptionsProvider.Default)!;
        var bundle = new GitHubRelease
        {
            Name = "community",
            TagName = "community-1",
            Assets = catalog.Apps.Select(a => new Asset { FileName = a.File }).ToList(),
        };
        return CommunityAppList.Expand(new[] { bundle }, catalog, requiresPartner: false);
    }

    [Fact]
    public void ClosedSourceAndSelfUpdating_AreParsed_CaseInsensitively()
    {
        var list = ExpandFlags();

        Assert.True(Named(list, "APL Launcher").IsSelfUpdating);
        Assert.False(Named(list, "APL Launcher").IsClosedSource);
        Assert.True(Named(list, "Reelo").IsClosedSource);
        Assert.False(Named(list, "Reelo").IsSelfUpdating);
        Assert.True(Named(list, "Reelo").Assets[0].IsClosedSource);

        var plain = Named(list, "Plain");
        Assert.False(plain.IsClosedSource || plain.IsSelfUpdating || plain.IsRoot || plain.IsAdult);
    }

    [Fact]
    public void Flags_CombineWithRoot_AndAGroupTakesThemFromAnyFile()
    {
        var list = ExpandFlags();

        var vetro = Named(list, "Vetro");
        Assert.True(vetro.IsRoot);
        Assert.True(vetro.IsSelfUpdating);
        Assert.True(vetro.IsClosedSource);

        var mixed = Named(list, "Mixed");
        Assert.True(mixed.IsClosedSource);
        Assert.True(mixed.IsSelfUpdating);
        Assert.False(mixed.Assets.Single(a => a.Variant == "Open").IsClosedSource);
        Assert.True(mixed.Assets.Single(a => a.Variant == "Closed").IsClosedSource);
    }

    [Fact]
    public void Visible_ShowsEverything_WithClosedSourceOn()
    {
        var list = ExpandFlags();

        var visible = CommunityAppList.Visible(list, showAdult: false).ToList();

        Assert.Equal(list.Count, visible.Count);
        Assert.Equal(2, Named(visible, "Mixed").Assets.Count);
        Assert.Same(Named(list, "Mixed"), Named(visible, "Mixed"));
    }

    [Fact]
    public void Visible_WithClosedSourceOff_HidesClosedFiles_KeepsMixedGroups()
    {
        var list = ExpandFlags();

        var visible = CommunityAppList.Visible(list, showAdult: false, showClosedSource: false).ToList();

        // A closed single file is gone, and so is a group whose every file is closed.
        Assert.DoesNotContain(visible, r => r.Name == "Reelo");
        Assert.DoesNotContain(visible, r => r.Name == "Vetro");
        Assert.DoesNotContain(visible, r => r.Name == "Shut");
        // Open apps stay as they are.
        Assert.Same(Named(list, "Plain"), Named(visible, "Plain"));
        Assert.Same(Named(list, "APL Launcher"), Named(visible, "APL Launcher"));

        // The mixed group stays with only its open file, and loses the closed file's flags.
        var mixed = Named(visible, "Mixed");
        var open = Assert.Single(mixed.Assets);
        Assert.Equal("Open", open.Variant);
        Assert.False(mixed.IsClosedSource);
        Assert.False(mixed.IsSelfUpdating);
        Assert.False(mixed.HasVariants);
        // The catalog itself is untouched, and a re-filter hands out the same entry.
        Assert.Equal(2, Named(list, "Mixed").Assets.Count);
        Assert.Same(mixed, Named(CommunityAppList.Visible(list, showAdult: false, showClosedSource: false).ToList(), "Mixed"));
    }
}
