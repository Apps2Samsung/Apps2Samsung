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
        Assert.Equal("other", UserSources.ToProvider(new UserSource { Url = "owner/repo", Category = "nonsense" })!.Category);
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
