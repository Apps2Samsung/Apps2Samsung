using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

public class UserSourcesTests
{
    [Theory]
    [InlineData("https://github.com/reisxd/TizenTube", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("github.com/reisxd/TizenTube/", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("https://www.github.com/reisxd/TizenTube.git", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("reisxd/TizenTube", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("https://github.com/reisxd/TizenTube/releases", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("https://github.com/reisxd/TizenTube/releases/latest", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("https://api.github.com/repos/reisxd/TizenTube", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    [InlineData("https://api.github.com/repos/reisxd/TizenTube/releases", "https://api.github.com/repos/reisxd/TizenTube/releases")]
    public void RepositoryLinksBecomeTheReleasesApiUrl(string typed, string api)
    {
        Assert.Equal(UserSourceKind.GitHubReleases, UserSources.Classify(typed));
        Assert.Equal(api, UserSources.ApiUrl(typed));
        Assert.Equal("TizenTube", UserSources.DefaultName(typed));
    }

    [Theory]
    [InlineData("https://github.com/reisxd/TizenTube/releases/tag/v3.1.0", "https://api.github.com/repos/reisxd/TizenTube/releases/tags/v3.1.0")]
    [InlineData("https://api.github.com/repos/reisxd/TizenTube/releases/tags/v3.1.0", "https://api.github.com/repos/reisxd/TizenTube/releases/tags/v3.1.0")]
    [InlineData("https://github.com/jeppevinkel/jellyfin-tizen-builds/releases/tag/2024-10-27-1821", "https://api.github.com/repos/jeppevinkel/jellyfin-tizen-builds/releases/tags/2024-10-27-1821")]
    public void ReleasePagesBecomeTheTagApiUrl(string typed, string api)
    {
        Assert.Equal(UserSourceKind.GitHubReleaseTag, UserSources.Classify(typed));
        Assert.Equal(api, UserSources.ApiUrl(typed));
    }

    [Theory]
    [InlineData("https://example.com/nightly/MyApp.wgt", "MyApp.wgt")]
    [InlineData("http://192.168.1.20:8080/builds/My%20App.TPK?token=abc", "My App.TPK")]
    [InlineData("https://github.com/owner/repo/releases/download/v1.0/App.wgt", "App.wgt")]
    public void FileLinksStayAsTheyAre(string typed, string file)
    {
        Assert.Equal(UserSourceKind.DirectFile, UserSources.Classify(typed));
        Assert.True(UserSources.IsDirectFile(typed));
        Assert.Equal(typed, UserSources.ApiUrl(typed));
        Assert.Equal(file, UserSources.FileName(typed));
        Assert.Equal(Path.GetFileNameWithoutExtension(file), UserSources.DefaultName(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("https://github.com/reisxd")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://example.com/page.html")]
    [InlineData("ftp://example.com/App.wgt")]
    [InlineData("https://api.github.com/users/reisxd")]
    public void AnythingElseIsRefused(string typed)
    {
        Assert.Equal(UserSourceKind.None, UserSources.Classify(typed));
        Assert.Null(UserSources.ApiUrl(typed));
        Assert.Null(UserSources.ToProvider(new UserSource { Url = typed }));
        var errors = UserSources.Validate(new UserSource { Url = typed });
        Assert.Equal("errUserSourceUrl", errors[UserSources.FieldUrl]);
    }

    [Fact]
    public void ProviderCarriesNameCategoryPartnerAndAStableUserId()
    {
        var source = new UserSource { Name = " My build ", Url = "owner/repo", Category = "IPTV", Partner = true };
        var provider = UserSources.ToProvider(source)!;
        Assert.Equal("My build", provider.DisplayName);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases", provider.Url);
        Assert.Equal("iptv", provider.Category);
        Assert.Equal("partner", provider.CertLevel);
        Assert.Equal(1, provider.Take);
        Assert.False(provider.ExpandAssets);
        Assert.True(provider.IsUserSource);
        Assert.StartsWith(UserSources.IdPrefix, provider.Id);
        // The same repository typed another way is the same provider.
        Assert.Equal(provider.Id, UserSources.ToProvider(new UserSource { Url = "https://github.com/Owner/Repo/" })!.Id);
        Assert.NotEqual(provider.Id, UserSources.ToProvider(new UserSource { Url = "owner/other" })!.Id);
    }

    [Fact]
    public void BlankNameTakesTheRepositoryOrFile()
    {
        Assert.Equal("repo", UserSources.ToProvider(new UserSource { Url = "owner/repo" })!.DisplayName);
        Assert.Equal("Nightly", UserSources.ToProvider(new UserSource { Url = "https://x.test/Nightly.wgt" })!.DisplayName);
        // Kept as written (#1054): the catalog may add the category later; unknown is shown under "other".
        Assert.Equal("other", AppCategories.Normalize(UserSources.ToProvider(new UserSource { Url = "owner/repo", Category = "nonsense" })!.Category));
        Assert.Equal("other", UserSources.ToProvider(new UserSource { Url = "owner/repo", Category = "Not An Id!" })!.Category);
    }

    [Fact]
    public void DuplicateOfAnExistingSourceIsReported()
    {
        var existing = new[] { new UserSource { Url = "https://github.com/owner/repo" } };
        var errors = UserSources.Validate(new UserSource { Url = "owner/repo" }, existing);
        Assert.Equal("errUserSourceDuplicate", errors[UserSources.FieldUrl]);
        // Editing a source is not a duplicate of itself.
        Assert.Empty(UserSources.Validate(existing[0], existing));
        Assert.Empty(UserSources.Validate(new UserSource { Url = "github.com/owner/repo/releases/tag/v1" }, existing));
    }

    [Fact]
    public void MergeAppendsUserProvidersAndSkipsWhatTheManifestAlreadyLists()
    {
        var manifest = new[]
        {
            new ProviderEntry { Id = "tizentube", Url = "https://api.github.com/repos/reisxd/TizenTube/releases" },
        };
        var sources = new[]
        {
            new UserSource { Url = "https://github.com/reisxd/TizenTube" }, // already in the manifest
            new UserSource { Url = "owner/repo" },
            new UserSource { Url = "https://github.com/owner/repo" }, // the same twice
            new UserSource { Url = "https://x.test/Nightly.wgt" },
            new UserSource { Url = "garbage" },
        };
        var merged = UserSources.Merge(manifest, sources);
        Assert.Equal(new[] { "tizentube" }, merged.Take(1).Select(p => p.Id));
        Assert.Equal(3, merged.Count);
        Assert.All(merged.Skip(1), p => Assert.True(p.IsUserSource));
        Assert.Equal("https://api.github.com/repos/owner/repo/releases", merged[1].Url);
        Assert.Equal("https://x.test/Nightly.wgt", merged[2].Url);
        Assert.False(merged[0].IsUserSource);
    }

    [Fact]
    public void JsonRoundTripsAndToleratesJunk()
    {
        var sources = new List<UserSource>
        {
            new() { Name = "Build", Url = "owner/repo", Category = "media", Partner = true },
            new() { Url = "https://x.test/Nightly.wgt" },
        };
        var json = UserSources.Serialize(sources);
        var back = UserSources.Parse(json);
        Assert.Equal(2, back.Count);
        Assert.Equal("Build", back[0].Name);
        Assert.Equal("owner/repo", back[0].Url);
        Assert.Equal("media", back[0].Category);
        Assert.True(back[0].Partner);
        Assert.Equal(string.Empty, back[1].Name);
        Assert.False(back[1].Partner);

        Assert.Equal(string.Empty, UserSources.Serialize(new List<UserSource>()));
        Assert.Empty(UserSources.Parse(null));
        Assert.Empty(UserSources.Parse(""));
        Assert.Empty(UserSources.Parse("{not json"));
        Assert.Empty(UserSources.Parse("[{\"name\":\"no url\"}]"));
    }
}

public class UserSourceFeedTests
{
    private const string Feed = """
        {
          "schemaVersion": 2,
          "providers": [
            { "id": "mine", "url": "https://api.github.com/repos/dev/app/releases", "displayName": "My App", "category": "games",
              "buildInfo": { "name": "My App", "description": "A game." } },
            { "id": "nightly", "url": "https://dev.example/nightly/Build.wgt", "displayName": "Nightly" },
            { "id": "partner-one", "url": "https://api.github.com/repos/dev/vpn/releases", "displayName": "VPN", "certLevel": "partner" },
            { "id": "already", "url": "https://api.github.com/repos/reisxd/TizenTube/releases", "displayName": "Dup" },
            { "id": "broken", "url": "" }
          ]
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    [Theory]
    [InlineData("https://dev.example/apps.json")]
    [InlineData("https://raw.githubusercontent.com/dev/app/main/third-party-apps.json")]
    [InlineData("dev.example/feeds/Apps.JSON?v=2")]
    public void JsonLinksAreFeeds(string typed)
    {
        Assert.Equal(UserSourceKind.Feed, UserSources.Classify(typed));
        Assert.True(UserSources.IsFeed(typed));
        Assert.Null(UserSources.ToProvider(new UserSource { Url = typed }));
        Assert.Empty(UserSources.Validate(new UserSource { Url = typed }));
        Assert.Equal(typed.Contains("raw.githubusercontent") ? "raw.githubusercontent.com" : "dev.example", UserSources.DefaultName(typed));
    }

    [Fact]
    public void FeedProvidersAreTheUsersOwnWithIdsUnderTheFeed()
    {
        var source = new UserSource { Url = "https://dev.example/apps.json" };
        var providers = UserSources.FeedProviders(source, Feed);
        Assert.Equal(4, providers.Count); // the one without a URL is dropped
        Assert.All(providers, p => Assert.True(p.IsUserSource));
        Assert.All(providers, p => Assert.StartsWith(UserSources.Id(source) + "-", p.Id));
        Assert.Equal(UserSources.Id(source) + "-mine", providers[0].Id);
        Assert.Equal("My App", providers[0].DisplayName);
        Assert.Equal("games", providers[0].Category);
        Assert.Equal("A game.", providers[0].BuildInfo?.Description);
        Assert.Equal("other", providers[1].Category);
        Assert.Equal("partner", providers[2].CertLevel);
        Assert.Equal(string.Empty, providers[0].CertLevel);
    }

    [Fact]
    public void FeedPartnerFlagCoversProvidersThatDoNotSay()
    {
        var providers = UserSources.FeedProviders(new UserSource { Url = "https://dev.example/apps.json", Partner = true }, Feed);
        Assert.All(providers, p => Assert.Equal("partner", p.CertLevel));
    }

    [Fact]
    public void BareArrayAndGarbage()
    {
        var source = new UserSource { Url = "https://dev.example/apps.json" };
        var bare = UserSources.FeedProviders(source, """[ { "url": "https://api.github.com/repos/a/b/releases" }, { "url": "https://api.github.com/repos/c/d/releases" } ]""");
        Assert.Equal(2, bare.Count);
        Assert.Equal(UserSources.Id(source) + "-1", bare[0].Id);
        Assert.Equal(UserSources.Id(source) + "-2", bare[1].Id);
        Assert.Empty(UserSources.FeedProviders(source, "{ not json"));
        Assert.Empty(UserSources.FeedProviders(source, "{ \"providers\": [] }"));
    }

    [Fact]
    public async Task ResolveReadsFeedsAndSkipsWhatIsAlreadyListed()
    {
        var http = new HttpClient(new Handler(request => request.RequestUri!.AbsoluteUri switch
        {
            "https://dev.example/apps.json" => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Feed) },
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        }));
        var manifest = new[] { new ProviderEntry { Id = "tizentube", Url = "https://api.github.com/repos/reisxd/TizenTube/releases" } };
        var sources = new[]
        {
            new UserSource { Url = "owner/repo" },
            new UserSource { Url = "https://dev.example/apps.json" },
            new UserSource { Url = "https://gone.example/apps.json" }, // 404: skipped, nothing else lost
            new UserSource { Url = "https://dev.example/nightly/Build.wgt" }, // the feed lists this one too: once
        };

        var providers = await UserSources.ResolveAsync(http, manifest, sources);

        Assert.Equal(new[]
        {
            "https://api.github.com/repos/reisxd/TizenTube/releases",
            "https://api.github.com/repos/owner/repo/releases",
            "https://dev.example/nightly/Build.wgt",
            "https://api.github.com/repos/dev/app/releases",
            "https://api.github.com/repos/dev/vpn/releases",
        }, providers.Select(p => p.Url));
        Assert.False(providers[0].IsUserSource);
        Assert.All(providers.Skip(1), p => Assert.True(p.IsUserSource));
        // The direct link typed by the user won, the feed's copy of it was skipped.
        Assert.StartsWith(UserSources.IdPrefix, providers[2].Id);
        Assert.DoesNotContain("-", providers[2].Id.Substring(UserSources.IdPrefix.Length));
    }
}
