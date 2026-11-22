using Apps2Samsung.Catalog;
using Apps2Samsung.Localization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

// What a newer catalog brought, as the list on screen sees it (#1157).
public class CatalogChangesTests
{
    private static readonly LocalizationCatalog English = new();

    private static GitHubRelease App(string name, string? packageVersion, string tag = "2026-11-22-0305", string? source = "https://github.com/a/b") => new()
    {
        Name = name,
        TagName = tag,
        SourceUrl = source,
        Assets = { new Asset { FileName = name + ".wgt", PackageVersion = packageVersion } },
    };

    [Fact]
    public void A_first_load_is_not_news()
    {
        var changes = CatalogChanges.Between(Array.Empty<GitHubRelease>(), new[] { App("Jellyfin", "1.0.0") });

        Assert.False(changes.Any);
        Assert.Equal(string.Empty, changes.Describe(English.GetString));
    }

    [Fact]
    public void An_app_that_was_not_there_is_new()
    {
        var before = new[] { App("Jellyfin", "1.0.0") };
        var after = new[] { App("Jellyfin", "1.0.0"), App("Moonfin", "0.4.0") };

        var changes = CatalogChanges.Between(before, after);

        Assert.True(changes.IsNew(after[1]));
        Assert.False(changes.IsNew(after[0]));
        Assert.Equal(0, changes.NewVersions);
        Assert.Equal("Catalog updated: 1 new app", changes.Describe(English.GetString));
    }

    // The community bundle gets a new release tag with every rebuild, changed or not.
    [Fact]
    public void A_new_release_tag_alone_is_not_a_new_version()
    {
        var before = new[] { App("Jellyfin", "1.0.0", tag: "2026-11-22-0305") };
        var after = new[] { App("Jellyfin", "1.0.0", tag: "2026-11-22-0335") };

        Assert.False(CatalogChanges.Between(before, after).Any);
    }

    [Fact]
    public void A_higher_package_version_is_a_new_version()
    {
        var before = new[] { App("Jellyfin", "1.0.0"), App("Flixor", "2.0.0") };
        var after = new[] { App("Jellyfin", "1.1.0"), App("Flixor", "2.0.0") };

        var changes = CatalogChanges.Between(before, after);

        Assert.Equal(1, changes.NewVersions);
        Assert.Empty(changes.NewApps);
        Assert.Equal("Catalog updated: 1 new version", changes.Describe(English.GetString));
    }

    [Fact]
    public void Without_package_versions_the_tag_decides()
    {
        var before = new[] { App("Nightly", null, tag: "v1") };
        var after = new[] { App("Nightly", null, tag: "v2") };

        Assert.Equal(1, CatalogChanges.Between(before, after).NewVersions);
    }

    // A provider that lists its history shows several entries under one name: one app.
    [Fact]
    public void Several_versions_of_one_app_count_once()
    {
        var before = new[] { App("Jellyfin", null, tag: "a"), App("Jellyfin", null, tag: "b") };
        var after = new[] { App("Jellyfin", null, tag: "b"), App("Jellyfin", null, tag: "c") };

        var changes = CatalogChanges.Between(before, after);

        Assert.Empty(changes.NewApps);
        Assert.Equal(1, changes.NewVersions);
    }

    [Fact]
    public void The_same_name_from_another_source_is_another_app()
    {
        var before = new[] { App("Player", "1.0.0", source: "https://github.com/a/player") };
        var after = new[] { App("Player", "1.0.0", source: "https://github.com/a/player"), App("Player", "1.0.0", source: "https://github.com/z/player") };

        Assert.Single(CatalogChanges.Between(before, after).NewApps);
    }

    [Fact]
    public void The_line_names_both_counts()
    {
        var changes = new CatalogChanges(new HashSet<string> { "a", "b" }, 3);

        Assert.Equal("Catalog updated: 2 new apps, 3 new versions", changes.Describe(English.GetString));
    }

    [Fact]
    public void New_apps_keep_their_label_over_a_later_refresh()
    {
        var first = new CatalogChanges(new HashSet<string> { "a" }, 2);
        var second = new CatalogChanges(new HashSet<string> { "b" }, 0);

        var both = second.After(first);

        Assert.Equal(2, both.NewApps.Count);
        Assert.Equal(0, both.NewVersions);
    }
}
