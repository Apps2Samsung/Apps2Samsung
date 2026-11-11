using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

public class CatalogPreviewsTests : IDisposable
{
    // build-catalog.sh copies preview_image onto each of a package's files; one group has it on
    // only one of its files, one entry has a plain-http URL the client must not use.
    private const string CatalogJson = """
        {
          "schemaVersion": 1,
          "apps": [
            { "file": "Film-Merkezi.wgt", "name": "Film Merkezi", "category": "streaming", "repo": "a/b",
              "preview_image": "https://raw.githubusercontent.com/a/b/main/docs/preview.png" },
            { "file": "Fork-A.wgt", "name": "Fork", "group": "Forked App", "category": "games", "repo": "a/c" },
            { "file": "Fork-B.wgt", "name": "Fork", "group": "Forked App", "category": "games", "repo": "a/c",
              "preview_image": "https://example.org/fork.png" },
            { "file": "Plain.wgt", "name": "Plain", "category": "other", "repo": "a/d",
              "preview_image": "http://example.org/plain.png" },
            { "file": "Doom.wgt", "name": "Doom", "category": "games", "repo": "a/e" }
          ]
        }
        """;

    private static readonly ProviderManifest Manifest = new()
    {
        CommunityApps =
        {
            new CommunityAppEntry { MatchName = "Film Merkezi", PreviewImage = "https://old.example/film.png" },
            new CommunityAppEntry { MatchName = "Doom", PreviewImage = "https://old.example/doom.png" },
        },
    };

    public void Dispose() => CatalogPreviews.Use(new CommunityCatalog());

    private static CommunityCatalog Parse() =>
        JsonSerializer.Deserialize<CommunityCatalog>(CatalogJson, JsonSerializerOptionsProvider.Default)!;

    private static List<GitHubRelease> Expand(CommunityCatalog catalog)
    {
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
    public void CatalogPreview_IsStamped_AndWinsOverTheManifestList()
    {
        var list = Expand(Parse());

        var film = Named(list, "Film Merkezi");
        Assert.Equal("https://raw.githubusercontent.com/a/b/main/docs/preview.png", film.PreviewImage);
        Assert.Equal(film.PreviewImage, CatalogPreviews.For(Manifest, film));
    }

    [Fact]
    public void AGroup_TakesThePreviewOfTheFileThatHasOne()
    {
        Assert.Equal("https://example.org/fork.png", Named(Expand(Parse()), "Forked App").PreviewImage);
    }

    [Fact]
    public void WithoutACatalogPreview_TheManifestListStillApplies()
    {
        var doom = Named(Expand(Parse()), "Doom");

        Assert.Null(doom.PreviewImage);
        Assert.Equal("https://old.example/doom.png", CatalogPreviews.For(Manifest, doom));
    }

    [Fact]
    public void APlainHttpPreview_IsIgnored()
    {
        var plain = Named(Expand(Parse()), "Plain");

        Assert.Null(plain.PreviewImage);
        Assert.Null(CatalogPreviews.For(null, plain));
    }

    [Fact]
    public void Community_LooksUpByNameAndGroup_AfterUse()
    {
        Assert.Null(CatalogPreviews.Community("Film Merkezi"));

        CatalogPreviews.Use(Parse());

        Assert.Equal("https://raw.githubusercontent.com/a/b/main/docs/preview.png", CatalogPreviews.Community("film merkezi"));
        Assert.Equal("https://example.org/fork.png", CatalogPreviews.Community("Forked App"));
        Assert.Null(CatalogPreviews.Community("Plain"));
        Assert.Null(CatalogPreviews.Community("Doom"));

        // No catalog at all keeps what was there.
        CatalogPreviews.Use(null);
        Assert.NotNull(CatalogPreviews.Community("Film Merkezi"));
    }

    [Fact]
    public void PreviewImage_RoundTripsThroughTheSnapshot()
    {
        var json = JsonSerializer.Serialize(Parse(), JsonSerializerOptionsProvider.Default);
        var back = JsonSerializer.Deserialize<CommunityCatalog>(json, JsonSerializerOptionsProvider.Default)!;

        Assert.Equal("https://raw.githubusercontent.com/a/b/main/docs/preview.png", back.Apps[0].PreviewImage);
        Assert.DoesNotContain("\"preview_image\":null", json);
    }
}
