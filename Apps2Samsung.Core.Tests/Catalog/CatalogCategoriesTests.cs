using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;
using Apps2Samsung.Design;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

// AppCategories.Use swaps process-wide state, so these run on their own, after the parallel tests.
[CollectionDefinition(nameof(CatalogCategoriesTests), DisableParallelization = true)]
public sealed class CatalogCategoriesCollection;

/// <summary>
/// The community catalog decides the categories, their order, names and tile colours (#1054), so
/// one can be added there without an Apps2Samsung release.
/// </summary>
[Collection(nameof(CatalogCategoriesTests))]
public sealed class CatalogCategoriesTests : IDisposable
{
    // "launcher" is new and sits between iptv and streaming; "other" is not last.
    private const string WithLauncher = """
        {
          "schemaVersion": 1,
          "categories": { "media": "Media servers & players", "iptv": "IPTV & Live TV", "launcher": "Launchers",
                          "streaming": "Streaming", "other": "Other", "tools": "Tools & system" },
          "category_colors": { "media": "#2B59FF", "launcher": "#12b886", "tools": "nope" },
          "apps": [
            { "file": "Home.wgt", "name": "Home", "category": "launcher", "repo": "a/home" },
            { "file": "Kodi.wgt", "name": "Kodi", "category": "media", "repo": "a/kodi" },
            { "file": "Mystery.wgt", "name": "Mystery", "category": "karaoke", "repo": "a/mystery" }
          ],
          "removed": [ { "name": "Gone", "repo": "a/gone", "category": "launcher", "reason": "takedown" } ]
        }
        """;

    private const string WithoutColors = """
        { "schemaVersion": 1, "categories": { "games": "Games & emulators", "launcher": "Launchers", "other": "Other" }, "apps": [] }
        """;

    // A bundle built before categories were written.
    private const string OldBundle = """
        { "schemaVersion": 1, "apps": [ { "file": "Home.wgt", "name": "Home", "category": "launcher", "repo": "a/home" } ] }
        """;

    public void Dispose() => AppCategories.Use(new CommunityCatalog());

    private static CommunityCatalog Parse(string json) =>
        JsonSerializer.Deserialize<CommunityCatalog>(json, JsonSerializerOptionsProvider.Default)!;

    private static List<GitHubRelease> Expand(CommunityCatalog catalog) =>
        CommunityAppList.Expand(new[]
        {
            new GitHubRelease { Name = "community", TagName = "community-1", Assets = catalog.Apps.Select(a => new Asset { FileName = a.File }).ToList() },
        }, catalog, requiresPartner: false);

    private static string Echo(string key) => key;

    [Fact]
    public void Catalog_categories_keep_their_order_and_other_stays_in()
    {
        AppCategories.Use(Parse(WithLauncher));

        Assert.Equal(new[] { "media", "iptv", "launcher", "streaming", "other", "tools" }, AppCategories.Ordered);
    }

    [Fact]
    public void A_new_category_shows_with_the_catalog_name_and_colour()
    {
        AppCategories.Use(Parse(WithLauncher));

        Assert.Equal("launcher", AppCategories.Normalize("Launcher"));
        Assert.Equal("Launchers", AppCategories.DisplayName("launcher", Echo));
        Assert.Null(AppCategories.LocalizationKey("launcher"));
        Assert.Equal("#12B886", AppTiles.Color("launcher", "Home").Light);
        // A builtin id keeps its translation.
        Assert.Equal("Vertaald", AppCategories.DisplayName("media", k => k == "categoryMedia" ? "Vertaald" : k));
        // A colour that isn't #RRGGBB is ignored; tools falls back to its token.
        Assert.Null(AppCategories.CatalogColor("tools"));
        Assert.Equal(DesignTokens.Colors.TileTools, AppTiles.Color("tools", "Anything"));
    }

    [Fact]
    public void Apps_are_grouped_by_the_catalog_categories_and_unknown_lands_in_other()
    {
        var catalog = Parse(WithLauncher);
        var apps = Expand(catalog);
        AppCategories.Use(catalog);

        // Stored as written, resolved when shown.
        Assert.Equal("karaoke", apps.Single(a => a.Name == "Mystery").Category);
        Assert.Equal(
            new[] { ("all", 3), ("media", 1), ("launcher", 1), ("other", 1) },
            CommunityAppList.Counts(apps).Select(c => (c.Id, c.Count)));
        Assert.Equal(new[] { "Mystery" }, CommunityAppList.Filter(apps, "other").Select(a => a.Name));
        Assert.Equal("Other", AppCategories.DisplayName("karaoke", Echo));
    }

    [Fact]
    public void An_id_read_before_the_catalog_arrived_still_finds_its_category()
    {
        // Providers load in parallel with the community catalog.
        var early = AppCategories.Clean("launcher");
        AppCategories.Use(Parse(WithLauncher));

        Assert.Equal("launcher", AppCategories.Normalize(early));
    }

    [Fact]
    public void Without_category_colors_new_categories_use_the_palette()
    {
        AppCategories.Use(Parse(WithoutColors));

        Assert.Equal(new[] { "games", "launcher", "other" }, AppCategories.Ordered);
        Assert.Null(AppCategories.CatalogColor("launcher"));
        Assert.Equal(DesignTokens.Colors.All[AppTiles.ColorToken("launcher", "Home")], AppTiles.Color("launcher", "Home"));
        Assert.Equal(DesignTokens.Colors.TileGames, AppTiles.Color("games", "Doom"));
        // A category the catalog dropped is no longer offered.
        Assert.Equal("other", AppCategories.Normalize("media"));
    }

    [Fact]
    public void An_old_bundle_brings_back_the_built_in_seven()
    {
        AppCategories.Use(Parse(WithLauncher));
        AppCategories.Use(Parse(OldBundle));

        Assert.Equal(new[] { "media", "iptv", "streaming", "games", "casting", "tools", "other" }, AppCategories.Ordered);
        Assert.Equal("other", AppCategories.Normalize("launcher"));
        Assert.Equal("Media servers & players", AppCategories.DisplayName("media", Echo));
    }

    [Fact]
    public void No_catalog_at_all_keeps_the_list_that_was_there()
    {
        AppCategories.Use(Parse(WithLauncher));
        AppCategories.Use(null);

        Assert.Contains("launcher", AppCategories.Ordered);
    }

    [Fact]
    public void Other_is_added_when_the_catalog_leaves_it_out()
    {
        AppCategories.Use(Parse("""{ "categories": { "launcher": "Launchers", "BAD ID": "x", "all": "y" }, "apps": [] }"""));

        Assert.Equal(new[] { "launcher", "other" }, AppCategories.Ordered);
    }

    [Fact]
    public void The_snapshot_round_trips_colors_order_and_raw_ids()
    {
        var catalog = Parse(WithLauncher);
        var json = JsonSerializer.Serialize(catalog, JsonSerializerOptionsProvider.Indented);
        var again = Parse(json);

        Assert.Contains("\"category_colors\"", json);
        Assert.Equal(catalog.Categories.Keys, again.Categories.Keys);
        Assert.Equal("#12b886", again.CategoryColors!["launcher"]);
        Assert.Equal("launcher", again.Apps.Single(a => a.Name == "Home").Category);
        Assert.Equal("karaoke", again.Apps.Single(a => a.Name == "Mystery").Category);
        Assert.Equal("launcher", again.Removed!.Single().Category);
        Assert.DoesNotContain("category_colors", JsonSerializer.Serialize(Parse(OldBundle), JsonSerializerOptionsProvider.Indented));
    }

    [Theory]
    [InlineData("launcher", true)]
    [InlineData("media", true)]
    [InlineData("Not An Id!", false)]
    public void A_repository_may_file_a_provider_under_any_category_id(string category, bool accepted)
    {
        // The payload of the valid test vector (everything after the signature line).
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Catalog", "Repositories", "Fixtures", "valid.signed"));
        var payload = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(text[(text.IndexOf('\n') + 1)..])!;
        payload["providers"]![0]!["category"] = category;

        var error = RepositorySchema.Check(payload);

        Assert.Equal(accepted, error is null);
        Assert.Equal(accepted ? category : "other", AppCategories.Clean(category));
    }
}
