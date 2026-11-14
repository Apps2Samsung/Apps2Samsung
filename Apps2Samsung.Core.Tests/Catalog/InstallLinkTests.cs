using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

/// <summary>Install links (#1066): reading apps2samsung:// and the website form, and finding the app they name.</summary>
public class InstallLinkTests
{
    [Theory]
    [InlineData("apps2samsung://install?app=Jellyfin")]
    [InlineData("apps2samsung:install?app=Jellyfin")]
    [InlineData("apps2samsung:///install/?app=Jellyfin")]
    [InlineData("APPS2SAMSUNG://INSTALL?APP=Jellyfin")]
    [InlineData("  apps2samsung://install?app=Jellyfin  ")]
    [InlineData("https://apps2samsung.com/install?app=Jellyfin")]
    [InlineData("https://www.apps2samsung.com/install/?app=Jellyfin#top")]
    public void Reads_an_install_link(string text)
    {
        Assert.True(InstallLink.TryParse(text, out var link));
        Assert.Equal(new InstallLink(InstallLinkKind.Install, App: "Jellyfin"), link);
    }

    [Fact]
    public void Decodes_the_app_and_the_file()
    {
        Assert.True(InstallLink.TryParse("apps2samsung://install?app=Moonlight%20TV&file=Sonance3-Oblong.wgt", out var link));
        Assert.Equal("Moonlight TV", link!.App);
        Assert.Equal("Sonance3-Oblong.wgt", link.File);
        Assert.True(InstallLink.TryParse("apps2samsung://install?app=Moonlight+TV", out link));
        Assert.Equal("Moonlight TV", link!.App);
    }

    [Fact]
    public void Reads_a_source_link()
    {
        Assert.True(InstallLink.TryParse("apps2samsung://source?url=https%3A%2F%2Fexample.org%2Frepo%2Frepository.json", out var link));
        Assert.Equal(InstallLinkKind.Source, link!.Kind);
        Assert.Equal("https://example.org/repo/repository.json", link.SourceUrl);
        Assert.True(InstallLink.TryParse("https://apps2samsung.com/source?url=https://example.org/repository.json", out link));
        Assert.Equal("https://example.org/repository.json", link!.SourceUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Jellyfin")]
    [InlineData("apps2samsung://install")]
    [InlineData("apps2samsung://install?app=")]
    [InlineData("apps2samsung://install?app=%20")]
    [InlineData("apps2samsung://install?file=Jellyfin.wgt")]
    [InlineData("apps2samsung://install?app=A&app=B")]
    [InlineData("apps2samsung://install?app=Jellyfin&file=")]
    [InlineData("apps2samsung://install?app=Jelly%0Afin")]
    [InlineData("apps2samsung://uninstall?app=Jellyfin")]
    [InlineData("apps2samsung://source?url=http://example.org/repository.json")]
    [InlineData("apps2samsung://source?url=file:///etc/passwd")]
    [InlineData("apps2samsung://source?url=https://user:pw@example.org/r.json")]
    [InlineData("apps2samsung://source?url=example.org")]
    [InlineData("http://apps2samsung.com/install?app=Jellyfin")]
    [InlineData("https://apps2samsung.com:8443/install?app=Jellyfin")]
    [InlineData("https://apps2samsung.com.evil.example/install?app=Jellyfin")]
    [InlineData("https://evil.example/install?app=Jellyfin")]
    [InlineData("https://apps2samsung.com/catalog/install?app=Jellyfin")]
    public void Refuses_anything_else(string? text)
    {
        Assert.False(InstallLink.TryParse(text, out var link));
        Assert.Null(link);
    }

    [Fact]
    public void Refuses_an_overlong_app()
    {
        Assert.False(InstallLink.TryParse("apps2samsung://install?app=" + new string('a', 201), out _));
        Assert.True(InstallLink.TryParse("apps2samsung://install?app=" + new string('a', 200), out _));
    }

    [Theory]
    [InlineData("apps2samsung://install?app=x", true)]
    [InlineData("https://apps2samsung.com/install?app=x", true)]
    [InlineData("https://apps2samsung.com/whatever", true)]
    [InlineData("Jellyfin", false)]
    [InlineData("./Jellyfin.wgt", false)]
    public void Knows_a_link_when_it_sees_one(string text, bool expected) =>
        Assert.Equal(expected, InstallLink.LooksLikeLink(text));

    [Fact]
    public void Writes_both_forms_back()
    {
        var link = new InstallLink(InstallLinkKind.Install, App: "Moonlight TV", File: "a b.wgt");
        Assert.Equal("apps2samsung://install?app=Moonlight%20TV&file=a%20b.wgt", link.ToSchemeUrl());
        Assert.Equal("https://apps2samsung.com/install?app=Moonlight%20TV&file=a%20b.wgt", link.ToWebUrl());
        Assert.True(InstallLink.TryParse(link.ToWebUrl(), out var back));
        Assert.Equal(link, back);
        var source = new InstallLink(InstallLinkKind.Source, SourceUrl: "https://example.org/r.json?x=1&y=2");
        Assert.True(InstallLink.TryParse(source.ToSchemeUrl(), out back));
        Assert.Equal(source, back);
    }

    // ---- Resolve ----

    private static GitHubRelease App(string name, CatalogSource? source = null, params (string File, string? PackageId, string? AppId, string? Variant)[] files) => new()
    {
        Name = name,
        Source = source ?? CatalogSource.Official,
        Assets = files.Select(f => new Asset { FileName = f.File, PackageId = f.PackageId, AppId = f.AppId, Variant = f.Variant }).ToList(),
    };

    private static readonly List<GitHubRelease> Catalog =
    [
        App("Jellyfin", null, ("Jellyfin.wgt", "AprZAARz4r", "AprZAARz4r.Jellyfin", null), ("Jellyfin-Legacy.wgt", "AprZAARz4r", "AprZAARz4r.Jellyfin", "Legacy")),
        App("tacplayer", null, ("tacplayer.wgt", "AprZAARz4r", "AprZAARz4r.Jellyfin", null)),
        App("Sonance3", null, ("Sonance3.wgt", "Sonance3", "Sonance3.App", "Standard"), ("Sonance3-Oblong.wgt", "Sonance3", "Sonance3.App", "Oblong tile")),
        App("Moonlight", null, ("Moonlight.wgt", "MoonlightA", "MoonlightA.Moonlight", null)),
        App("Moonlight", new CatalogSource(CatalogSourceKind.Repository, "Someone's repo", "someone"), ("Moonlight.wgt", "MoonlightA", "MoonlightA.Moonlight", null)),
    ];

    private static InstallLinkMatch? Resolve(string text)
    {
        Assert.True(InstallLink.TryParse(text, out var link));
        return InstallLink.Resolve(link!, Catalog);
    }

    [Fact]
    public void Finds_an_app_by_its_exact_name_only()
    {
        var m = Resolve("apps2samsung://install?app=sonance3");
        Assert.Equal("Sonance3", m!.Name);
        Assert.Null(m.File);
        Assert.False(m.FileMissing);
        Assert.Null(Resolve("apps2samsung://install?app=Sonance"));
        Assert.Null(Resolve("apps2samsung://install?app=Nothing"));
    }

    [Fact]
    public void Finds_an_app_by_package_or_app_id()
    {
        var m = Resolve("apps2samsung://install?app=MoonlightA.Moonlight");
        Assert.Equal("Moonlight", m!.Name);
        Assert.Equal("Moonlight.wgt", m.File);
        // Both files of Sonance3 carry the id: the app's default stays.
        m = Resolve("apps2samsung://install?app=Sonance3.App");
        Assert.Equal("Sonance3", m!.Name);
        Assert.Null(m.File);
    }

    [Fact]
    public void An_id_two_apps_share_names_nothing()
    {
        // tacplayer kept Jellyfin's package id (#1028).
        Assert.Null(Resolve("apps2samsung://install?app=AprZAARz4r"));
        // The name still works.
        Assert.Equal("tacplayer", Resolve("apps2samsung://install?app=tacplayer")!.Name);
    }

    [Fact]
    public void Returns_every_source_that_offers_the_app()
    {
        var m = Resolve("apps2samsung://install?app=Moonlight");
        Assert.Equal(2, m!.Entries.Count);
    }

    [Theory]
    [InlineData("Sonance3-Oblong.wgt")]
    [InlineData("sonance3-oblong")]
    [InlineData("Oblong tile")]
    public void Pins_the_file_a_link_names(string file)
    {
        var m = Resolve("apps2samsung://install?app=Sonance3&file=" + Uri.EscapeDataString(file));
        Assert.Equal("Sonance3-Oblong.wgt", m!.File);
        Assert.False(m.FileMissing);
    }

    [Fact]
    public void Says_when_the_file_is_gone()
    {
        var m = Resolve("apps2samsung://install?app=Sonance3&file=Sonance3-Old.wgt");
        Assert.Equal("Sonance3", m!.Name);
        Assert.Null(m.File);
        Assert.True(m.FileMissing);
    }

    [Fact]
    public void A_source_link_resolves_to_no_app()
    {
        Assert.True(InstallLink.TryParse("apps2samsung://source?url=https://example.org/r.json", out var link));
        Assert.Null(InstallLink.Resolve(link!, Catalog));
    }
}
