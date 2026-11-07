using System.IO.Compression;
using Apps2Samsung.Install;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using static Apps2Samsung.Tests.Install.PackageIdsTests;

namespace Apps2Samsung.Tests.Install;

// #1028: a fork that kept another app's package id goes in under an id of its own, derived from its
// catalog entry so TV-Store (Apps2Samsung/TV-Store#84) arrives at the same one.
public sealed class PackageIdCollisionTests : IDisposable
{
    private const string Tv = "DUID-1";
    private const string JellyfinId = "AprZAARz4r";
    private const string TacplayerKey = "sfsdffesfsfg-cloud/tacplayer/tacplayer.wgt";

    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-collide-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // tacplayer: the Jellyfin client's ids, its own name, and a service launched by id from its JS.
    private string Tacplayer(string name = "tacplayer")
    {
        var path = Path.Combine(_dir, "tacplayer.wgt");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("config.xml").Open()))
            writer.Write($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <widget xmlns="http://www.w3.org/ns/widgets" xmlns:tizen="http://tizen.org/ns/widgets" id="http://example.org/tac" version="0.1.0">
                    <tizen:application id="{JellyfinId}.Jellyfin" package="{JellyfinId}" required_version="4.0"/>
                    <tizen:service id="{JellyfinId}.Proxy"><tizen:content src="service.js"/></tizen:service>
                    <name>{name}</name>
                </widget>
                """);
        using (var writer = new StreamWriter(zip.CreateEntry("main.js").Open()))
            writer.Write($"tizen.application.launchAppControl(c, '{JellyfinId}.Proxy');");
        return path;
    }

    private string NativePackage()
    {
        var path = Path.Combine(_dir, "native.tpk");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("tizen-manifest.xml").Open()))
            writer.Write("""
                <?xml version="1.0" encoding="utf-8"?>
                <manifest xmlns="http://tizen.org/ns/packages" package="org.example.player" version="1.0.0" api-version="6.0">
                    <ui-application appid="org.example.player" exec="player" type="dotnet">
                        <label>Player</label>
                    </ui-application>
                </manifest>
                """);
        return path;
    }

    private static InstalledApp App(string tizenId, string title) => new(title, null, tizenId, "1.0.0", "", true);

    private static readonly IReadOnlyList<InstalledApp> JellyfinOnTv = new[] { App($"{JellyfinId}.Jellyfin", "Jellyfin") };

    // Pinned: TV-Store implements the same derivation and must arrive at the same id. Checked
    // against an independent implementation (Python hashlib) when it was written.
    [Fact]
    public void DerivedId_IsTheSharedScheme()
    {
        Assert.Equal("ln9ooxgrYz", PackageIdCollisions.DerivedId(TacplayerKey));
        Assert.Equal("q3U656qtEg", PackageIdCollisions.DerivedId("x"));
        Assert.Matches("^[A-Za-z0-9]{10}$", PackageIdCollisions.DerivedId("anything/else.wgt"));
    }

    [Fact]
    public void CatalogKey_CommunityFile_IsRepoAndFile()
    {
        var release = new GitHubRelease { SourceUrl = "https://github.com/Apps2Samsung/tizen-community-packages" };
        var asset = new Asset { FileName = "tacplayer.wgt", CatalogRepo = "sfsdffesfsfg-cloud/tacplayer" };

        Assert.Equal(TacplayerKey, PackageIdCollisions.CatalogKey(release, asset));
    }

    [Fact]
    public void CatalogKey_ProviderRelease_UsesItsGitHubRepository()
    {
        var release = new GitHubRelease { SourceUrl = "https://github.com/jeppevinkel/jellyfin-tizen-builds" };
        var asset = new Asset { FileName = "Jellyfin.wgt" };

        Assert.Equal("jeppevinkel/jellyfin-tizen-builds/Jellyfin.wgt", PackageIdCollisions.CatalogKey(release, asset));
    }

    [Fact]
    public void CatalogKey_IndependentRepository_UsesTheRepositoryUrl()
    {
        var release = new GitHubRelease
        {
            Source = new CatalogSource(CatalogSourceKind.Repository, "Example", "example", null, "https://apps.example.org/repository.json.signed"),
            SourceUrl = "https://github.com/example/app",
        };
        var asset = new Asset { FileName = "app.wgt", CatalogRepo = "ignored/here" };

        Assert.Equal("https://apps.example.org/repository.json.signed/app.wgt", PackageIdCollisions.CatalogKey(release, asset));
    }

    [Fact]
    public void CommunityAppList_CarriesTheManifestRepoOntoEachFile()
    {
        var bundle = new GitHubRelease { Assets = new List<Asset> { new() { FileName = "tacplayer.wgt" } } };
        var catalog = new CommunityCatalog { Apps = { new CommunityCatalogEntry { File = "tacplayer.wgt", Name = "tacplayer", Repo = "sfsdffesfsfg-cloud/tacplayer" } } };

        var entry = Apps2Samsung.Catalog.CommunityAppList.Expand(new[] { bundle }, catalog, false).Single();

        Assert.Equal(TacplayerKey, PackageIdCollisions.CatalogKey(entry, entry.Assets[0]));
    }

    [Fact]
    public async Task Find_OtherNameUnderTheSameId_IsACollision()
    {
        var collision = PackageIdCollisions.Find(JellyfinOnTv, await WgtManifest.ReadNamesAsync(Tacplayer()));

        Assert.NotNull(collision);
        Assert.Equal("Jellyfin", collision!.OccupantName);
        Assert.Equal("tacplayer", collision.PackageName);
        Assert.False(collision.IsNative);
    }

    [Theory]
    [InlineData("Jellyfin")]
    [InlineData("  jellyfin ")]
    public async Task Find_SameName_IsTheSameApp(string name)
    {
        Assert.Null(PackageIdCollisions.Find(JellyfinOnTv, await WgtManifest.ReadNamesAsync(Tacplayer(name))));
    }

    [Fact]
    public async Task Find_UntitledOrAbsent_IsNoCollision()
    {
        var names = await WgtManifest.ReadNamesAsync(Tacplayer());

        Assert.Null(PackageIdCollisions.Find(new[] { App($"{JellyfinId}.Jellyfin", "") }, names));
        Assert.Null(PackageIdCollisions.Find(new[] { App("Other12345.App", "Other") }, names));
        // A longer id that merely starts with ours is another package.
        Assert.Null(PackageIdCollisions.Find(new[] { App($"{JellyfinId}X.App", "Other") }, names));
    }

    [Fact]
    public async Task Collision_RenamesToTheDerivedId_ServicesAndScriptsIncluded()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Tacplayer();
        var progress = new List<string>();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, CatalogKey: TacplayerKey), config, progress.Add, JellyfinOnTv);

        Assert.NotNull(rename);
        Assert.Equal("ln9ooxgrYz", rename!.NewId);
        Assert.Equal("Jellyfin", rename.CollidedWith);
        Assert.Equal(("ln9ooxgrYz.Jellyfin", "ln9ooxgrYz"), await WgtManifest.ReadIdsAsync(wgt));
        Assert.Contains("main.js", rename.FilesChanged);
        using (var zip = ZipFile.OpenRead(wgt))
        {
            var config_ = new StreamReader(zip.GetEntry("config.xml")!.Open()).ReadToEnd();
            Assert.Contains("<tizen:service id=\"ln9ooxgrYz.Proxy\"", config_);
            Assert.Contains("'ln9ooxgrYz.Proxy'", new StreamReader(zip.GetEntry("main.js")!.Open()).ReadToEnd());
        }
        Assert.Contains(progress, p => p.StartsWith("statusPackageIdSeparateApp|ln9ooxgrYz|Jellyfin", StringComparison.Ordinal));
        // Remembered like a #839 rename, so updates and the installed-apps view map it back.
        var record = PackageIdRenameStore.Find(config.PackageIdRenamesJson, Tv, JellyfinId);
        Assert.Equal("ln9ooxgrYz.Jellyfin", record?.NewAppId);
        Assert.Equal($"{JellyfinId}.Jellyfin", PackageIdRenameStore.OriginalAppIdOf(config.PackageIdRenamesJson, "ln9ooxgrYz.Jellyfin"));
    }

    [Fact]
    public async Task NoCollision_KeepsTheOwnId()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Tacplayer("Jellyfin");

        Assert.Null(await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, CatalogKey: TacplayerKey), config, installed: JellyfinOnTv));
        Assert.Null(await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, CatalogKey: TacplayerKey), config, installed: Array.Empty<InstalledApp>()));
        Assert.Equal(JellyfinId, (await WgtManifest.ReadIdsAsync(wgt)).PackageId);
        Assert.Equal(string.Empty, config.PackageIdRenamesJson);
    }

    // Installed from TV-Store under the derived id, no record here: the update lands on that copy.
    [Fact]
    public async Task DerivedIdAlreadyOnTheTv_IsUpdatedInPlace()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Tacplayer();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, CatalogKey: TacplayerKey), config,
            installed: new[] { App("ln9ooxgrYz.Jellyfin", "tacplayer") });

        Assert.Equal("ln9ooxgrYz", rename?.NewId);
        Assert.Null(rename?.CollidedWith);
    }

    // A TV that does not hand its DUID out still gets the rename; there is just nothing to remember.
    [Fact]
    public async Task NoDuid_StillRenames_RemembersNothing()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Tacplayer();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy("", CatalogKey: TacplayerKey), config, installed: JellyfinOnTv);

        Assert.Equal("ln9ooxgrYz", rename?.NewId);
        Assert.Equal(string.Empty, config.PackageIdRenamesJson);
    }

    [Fact]
    public async Task OwnId_TurnsTheAutomaticRenameOff()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Tacplayer();

        Assert.Null(await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, OwnId: true, CatalogKey: TacplayerKey), config, installed: JellyfinOnTv));
        Assert.Equal(JellyfinId, (await WgtManifest.ReadIdsAsync(wgt)).PackageId);
    }

    [Fact]
    public async Task NoListing_NoAutomaticRename()
    {
        var config = RenamesConfig.With(string.Empty);

        Assert.Null(await PackageIds.ApplyAsync(Tacplayer(), new PackageIdPolicy(Tv, CatalogKey: TacplayerKey), config, installed: null));
    }

    // A picked file has no catalog entry: keyed on its own id and name, so it is still stable.
    [Fact]
    public async Task NoCatalogKey_UsesTheLocalKey()
    {
        var config = RenamesConfig.With(string.Empty);

        var rename = await PackageIds.ApplyAsync(Tacplayer(), new PackageIdPolicy(Tv), config, installed: JellyfinOnTv);

        Assert.Equal(PackageIdCollisions.DerivedId(PackageIdCollisions.LocalKey(JellyfinId, "tacplayer")), rename?.NewId);
    }

    [Fact]
    public async Task NativePackage_IsNeverRenamed_ButIsReportedAsACollision()
    {
        var config = RenamesConfig.With(string.Empty);
        var tpk = NativePackage();
        var installed = new[] { App("org.example.player", "Someone else's player") };

        Assert.Null(await PackageIds.ApplyAsync(tpk, new PackageIdPolicy(Tv, CatalogKey: "a/b/native.tpk"), config, installed: installed));
        var collision = PackageIdCollisions.Find(installed, await WgtManifest.ReadNamesAsync(tpk));
        Assert.True(collision?.IsNative);
        Assert.Equal("Player", collision?.PackageName);
    }

    // The installed-apps view (#820): the renamed fork maps to its own catalog line, not the
    // Jellyfin client's, also when TV-Store renamed it and this client has no record.
    [Fact]
    public void InstalledUnderTheDerivedId_MatchesItsOwnCatalogEntry()
    {
        var jellyfin = new GitHubRelease
        {
            Name = "Jellyfin", PublishedAt = "2026-11-01T00:00:00Z", SourceUrl = "https://github.com/jeppevinkel/jellyfin-tizen-builds",
            Assets = { new Asset { FileName = "Jellyfin.wgt", AppId = $"{JellyfinId}.Jellyfin", PackageVersion = "10.10.0" } },
        };
        var tacplayer = new GitHubRelease
        {
            Name = "tacplayer", PublishedAt = "2026-11-01T00:00:00Z",
            Assets = { new Asset { FileName = "tacplayer.wgt", CatalogRepo = "sfsdffesfsfg-cloud/tacplayer", AppId = $"{JellyfinId}.Jellyfin", PackageVersion = "0.2.0" } },
        };
        var catalog = new[] { jellyfin, tacplayer };

        var match = Apps2Samsung.Catalog.InstalledAppUpdates.Find(new InstalledApp("tacplayer", null, "ln9ooxgrYz.Jellyfin", "0.1.0", "", true), catalog);

        Assert.Same(tacplayer, match?.Release);
        Assert.True(match?.HasUpdate);
        // The Jellyfin client under its own id is still matched as before.
        Assert.NotNull(Apps2Samsung.Catalog.InstalledAppUpdates.Find(new InstalledApp("Jellyfin", null, $"{JellyfinId}.Jellyfin", "10.10.0", "", true), catalog));
    }
}
