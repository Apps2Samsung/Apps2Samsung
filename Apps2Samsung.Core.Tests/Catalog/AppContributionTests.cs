using System.Text.Json;
using Apps2Samsung.Catalog;

namespace Apps2Samsung.Tests.Catalog;

public class AppContributionTests
{
    private static AppContribution Release() => new()
    {
        Name = "Flixor",
        Description = "Plex client for Tizen",
        Category = "media",
        Repo = "https://github.com/PatrickSt1991/flixor-tizen.git",
        Source = AppContribution.SourceRelease,
        Branch = "main",
        OutputName = "Flixor.wgt",
        Url = "https://ignored.example/x.wgt",
        ProjectPath = "ignored",
        License = "MIT",
    };

    [Fact]
    public void RepoUrlBecomesSlugAndFileName()
    {
        var app = Release();
        Assert.Equal("PatrickSt1991/flixor-tizen", app.RepoSlug);
        Assert.Equal("PatrickSt1991__flixor-tizen.json", app.FileName);
    }

    [Fact]
    public void ReleaseJsonHasOnlyReleaseFieldsInOrder()
    {
        var json = Release().ToPackageJson();
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "name", "description", "category", "repo", "source", "branch", "output_name" }, names);
        Assert.EndsWith("}\n", json);
        Assert.StartsWith("{\n  \"name\"", json);
        Assert.DoesNotContain("\r", json);
    }

    [Fact]
    public void DirectJsonHasUrlAndNoBranch()
    {
        var app = Release();
        app.Source = AppContribution.SourceDirect;
        app.Adult = true;
        using var doc = JsonDocument.Parse(app.ToPackageJson());
        var root = doc.RootElement;
        Assert.Equal("https://ignored.example/x.wgt", root.GetProperty("url").GetString());
        Assert.False(root.TryGetProperty("branch", out _));
        Assert.False(root.TryGetProperty("project_path", out _));
        Assert.True(root.GetProperty("adult").GetBoolean());
    }

    [Fact]
    public void ValidateMirrorsTheSchema()
    {
        Assert.Empty(Release().Validate());

        var bad = new AppContribution
        {
            Name = "A | B",
            Repo = "not a repo",
            Source = AppContribution.SourceBuild,
            OutputName = "app.zip",
        };
        var errors = bad.Validate();
        Assert.Equal("errContributeOneLine", errors["name"]);
        Assert.Equal("errContributeRequired", errors["description"]);
        Assert.Equal("errContributeRepo", errors["repo"]);
        Assert.Equal("errContributeRequired", errors["branch"]);
        Assert.Equal("errContributeOutputName", errors["output_name"]);
        Assert.Equal("errContributeRequired", errors["license"]);
        Assert.False(errors.ContainsKey("url"));

        bad.Source = AppContribution.SourceDirect;
        bad.Url = "ftp://x";
        Assert.Equal("errContributeUrl", bad.Validate()["url"]);
        Assert.False(bad.Validate().ContainsKey("branch"));
    }

    [Fact]
    public void LinksCarryTheFields()
    {
        var app = Release();
        Assert.StartsWith("https://github.com/Apps2Samsung/tizen-community-packages/new/main/packages?filename=PatrickSt1991__flixor-tizen.json&value=", app.PullRequestUrl);
        Assert.Contains(Uri.EscapeDataString("\"output_name\": \"Flixor.wgt\""), app.PullRequestUrl);

        var issue = app.IssueUrl;
        Assert.StartsWith("https://github.com/Apps2Samsung/tizen-community-packages/issues/new?template=contribute-app.yml&title=", issue);
        Assert.Contains("&license=MIT", issue);
        Assert.Contains("&notes=" + Uri.EscapeDataString("Category: media"), issue);
        Assert.DoesNotContain("&url=", issue);
        Assert.DoesNotContain("&project_path=", issue);
    }
}
