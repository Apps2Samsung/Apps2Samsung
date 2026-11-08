using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Install;
using Apps2Samsung.Localization;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Catalog;

/// <summary>Apps the community catalog stopped distributing (#1033): parsing, the snapshot round trip, matching and search.</summary>
public class RemovedAppsTests
{
    // The shape build-catalog.sh writes: one entry with ids, one without (an app disabled before ids were carried).
    private const string CatalogJson = """
        {
          "schemaVersion": 1,
          "categories": { "streaming": "Streaming" },
          "apps": [
            { "file": "Jellyfin.wgt", "name": "Jellyfin", "category": "streaming", "repo": "jellyfin/jellyfin-tizen",
              "version": "10.9.0", "app_id": "AprZAARz4r.Jellyfin", "package_id": "AprZAARz4r" }
          ],
          "removed": [
            { "name": "Bisnor", "repo": "nikan48g/Bisnor", "host": "github", "category": "streaming",
              "reason": "unlicensed-content", "note": "Gets its streams from unlicensed film servers.",
              "files": ["Bisnor.wgt"], "since": "2026-11-08",
              "package_ids": ["hnn00bisnr"], "app_ids": ["hnn00bisnr.BisnorCinema"] },
            { "name": "30nama", "repo": "msharifi99/30nama-tizenos", "host": "github", "category": "streaming",
              "reason": "unlicensed-service", "files": ["30nama.wgt"], "since": "2026-11-08" }
          ]
        }
        """;

    private static CommunityCatalog Parse(string json = CatalogJson) =>
        JsonSerializer.Deserialize<CommunityCatalog>(json, JsonSerializerOptionsProvider.Default)!;

    private static InstalledApp App(string tizenId) => new("Some app", null, tizenId, "1.0", "", true);

    [Fact]
    public void Catalog_json_removed_entries_parse_with_their_ids()
    {
        var removed = RemovedApps.From(Parse());

        Assert.Equal(2, removed.Count);
        var bisnor = removed[0];
        Assert.Equal("Bisnor", bisnor.Name);
        Assert.Equal("nikan48g/Bisnor", bisnor.Repo);
        Assert.Equal("unlicensed-content", bisnor.Reason);
        Assert.Equal("Gets its streams from unlicensed film servers.", bisnor.Note);
        Assert.Equal(new[] { "Bisnor.wgt" }, bisnor.Files);
        Assert.Equal("2026-11-08", bisnor.Since);
        Assert.Equal(new[] { "hnn00bisnr" }, bisnor.PackageIds);
        Assert.Equal(new[] { "hnn00bisnr.BisnorCinema" }, bisnor.AppIds);
        Assert.Null(removed[1].PackageIds);
        Assert.Null(removed[1].Note);
    }

    [Fact]
    public void Removed_survives_the_snapshot_into_resolved_json()
    {
        // tools/CatalogSnapshot: catalog.json read with Default, resolved.json written with Indented.
        var snapshot = new ResolvedCatalog { Providers = { new ResolvedProvider { Id = "community", Ok = true, Catalog = Parse() } } };
        var json = JsonSerializer.Serialize(snapshot, JsonSerializerOptionsProvider.Indented);

        Assert.Contains("\"removed\"", json);
        Assert.Contains("\"package_ids\"", json);
        Assert.Contains("\"app_ids\"", json);

        var back = JsonSerializer.Deserialize<ResolvedCatalog>(json, JsonSerializerOptionsProvider.Default)!;
        var removed = RemovedApps.From(back.Providers[0].Catalog);
        Assert.Equal(new[] { "Bisnor", "30nama" }, removed.Select(r => r.Name));
        Assert.Equal(new[] { "hnn00bisnr" }, removed[0].PackageIds);
        Assert.Equal("unlicensed-service", removed[1].Reason);
    }

    [Fact]
    public void A_catalog_without_removed_writes_no_removed_key()
    {
        var catalog = Parse(CatalogJson.Replace("\"removed\"", "\"ignored\""));
        Assert.Null(catalog.Removed);
        Assert.Empty(RemovedApps.From(catalog));
        Assert.DoesNotContain("\"removed\"", JsonSerializer.Serialize(catalog, JsonSerializerOptionsProvider.Indented));
    }

    [Fact]
    public void Removed_entries_never_reach_the_app_list()
    {
        var release = new GitHubRelease
        {
            Name = "community", TagName = "community-1",
            Assets = new() { new Asset { FileName = "Jellyfin.wgt" } },
        };
        var entries = CommunityAppList.Expand(new[] { release }, Parse(), requiresPartner: false);

        Assert.Equal(new[] { "Jellyfin" }, entries.Select(e => e.Name));
        Assert.Equal(new[] { ("all", 1), ("streaming", 1) }, CommunityAppList.Counts(entries).Select(c => (c.Id, c.Count)));
        Assert.Equal(2, CommunityAppList.Removed(Parse()).Count);
    }

    [Fact]
    public void Installed_app_matches_by_app_id_and_by_package_id()
    {
        var removed = RemovedApps.From(Parse());

        Assert.Equal("Bisnor", RemovedApps.Find(App("hnn00bisnr.BisnorCinema"), removed)?.Name);
        // Another application id in the same package still belongs to it.
        Assert.Equal("Bisnor", RemovedApps.Find(App("hnn00bisnr.Other"), removed)?.Name);
        Assert.Null(RemovedApps.Find(App("AprZAARz4r.Jellyfin"), removed));
    }

    [Fact]
    public void Installed_app_matches_through_the_rename_store()
    {
        var renames = PackageIdRenameStore.Write(new List<PackageIdRenameRecord>
        {
            new() { TvDuid = "tv", OriginalPackageId = "hnn00bisnr", NewPackageId = "Zz9Yy8Xx7W",
                    OriginalAppId = "hnn00bisnr.BisnorCinema", NewAppId = "Zz9Yy8Xx7W.BisnorCinema" },
        });

        Assert.Equal("Bisnor", RemovedApps.Find(App("Zz9Yy8Xx7W.BisnorCinema"), RemovedApps.From(Parse()), packageIdRenamesJson: renames)?.Name);
    }

    [Fact]
    public void Installed_app_matches_the_id_derived_from_repo_and_file()
    {
        // 30nama has no ids in the catalog; a copy installed under its derived id (#1028) is still found.
        var derived = PackageIdCollisions.DerivedId("msharifi99/30nama-tizenos/30nama.wgt");

        Assert.Equal("30nama", RemovedApps.Find(App(derived + ".Nama30TV"), RemovedApps.From(Parse()))?.Name);
    }

    [Fact]
    public void An_id_a_live_catalog_file_still_declares_is_not_called_removed()
    {
        var removed = RemovedApps.From(Parse());
        var live = new[]
        {
            new GitHubRelease { Name = "Bisnor fork", Assets = new() { new Asset { FileName = "Fork.wgt", AppId = "hnn00bisnr.BisnorCinema", PackageId = "hnn00bisnr" } } },
        };

        Assert.Null(RemovedApps.Find(App("hnn00bisnr.BisnorCinema"), removed, live));
    }

    [Theory]
    [InlineData("bis", new[] { "Bisnor" })]
    [InlineData("BISNOR", new[] { "Bisnor" })]
    [InlineData("30 nama", new[] { "30nama" })]
    [InlineData("b", new string[0])]
    [InlineData("  ", new string[0])]
    [InlineData("jellyfin", new string[0])]
    public void Search_names_removed_entries(string query, string[] expected)
    {
        Assert.Equal(expected, RemovedApps.Search(query, RemovedApps.From(Parse())).Select(r => r.Name));
    }

    [Fact]
    public void Named_is_exact_and_case_insensitive()
    {
        var removed = RemovedApps.From(Parse());
        Assert.Equal("Bisnor", RemovedApps.Named("bisnor", removed)?.Name);
        Assert.Null(RemovedApps.Named("bis", removed));
    }

    [Fact]
    public void The_date_reads_in_the_user_s_language_and_a_bad_one_is_left_out()
    {
        Assert.Equal("8 November 2026", RemovedApps.SinceText("2026-11-08", new System.Globalization.CultureInfo("en-GB")));
        Assert.Equal("8 november 2026", RemovedApps.SinceText("2026-11-08", new System.Globalization.CultureInfo("nl-NL")));
        Assert.Null(RemovedApps.SinceText("08-11-2026", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(RemovedApps.SinceText(null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Only_the_official_catalog_may_say_what_it_removed()
    {
        Assert.True(RemovedApps.IsOfficial(new ProviderEntry()));
        Assert.True(RemovedApps.IsOfficial(new ProviderEntry { Source = CatalogSource.Official }));
        Assert.False(RemovedApps.IsOfficial(new ProviderEntry { IsUserSource = true }));
        Assert.False(RemovedApps.IsOfficial(new ProviderEntry { Source = new CatalogSource(CatalogSourceKind.Repository, "Someone's repo") }));
    }

    [Fact]
    public void A_search_line_is_the_name_and_the_reason()
    {
        var english = new LocalizationCatalog();
        Assert.Equal("Bisnor · Removed: it ships streams or links to content it has no licence for.",
            RemovedApps.SearchLine(RemovedApps.From(Parse())[0], english.GetString));
    }

    [Theory]
    [InlineData("unlicensed-content", "lblRemovedReasonUnlicensedContent")]
    [InlineData("unlicensed-service", "lblRemovedReasonUnlicensedService")]
    [InlineData("unlicensed-game-data", "lblRemovedReasonUnlicensedGameData")]
    [InlineData("takedown", "lblRemovedReasonTakedown")]
    [InlineData("superseded", "lblRemovedReasonSuperseded")]
    [InlineData("upstream-gone", "lblRemovedReasonUpstreamGone")]
    [InlineData("Takedown ", "lblRemovedReasonTakedown")]
    [InlineData("something-new", "lblRemovedReasonUnknown")]
    [InlineData(null, "lblRemovedReasonUnknown")]
    public void Reasons_map_to_keys_that_exist_in_en_json(string? reason, string key)
    {
        Assert.Equal(key, RemovedApps.ReasonKey(reason));
        var english = new LocalizationCatalog();
        Assert.NotEqual(key, english.GetString(key));
        Assert.NotEqual(RemovedApps.BadgeKey, english.GetString(RemovedApps.BadgeKey));
        Assert.NotEqual(RemovedApps.FooterKey, english.GetString(RemovedApps.FooterKey));
    }
}
