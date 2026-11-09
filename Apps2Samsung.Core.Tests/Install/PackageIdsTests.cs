using System.IO.Compression;
using System.Reflection;
using Apps2Samsung.Configuration;
using Apps2Samsung.Install;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Install;

// PackageIds.ApplyAsync (#839): the rename remembered for a TV, and the way back from it.
public sealed class PackageIdsTests : IDisposable
{
    private const string Tv = "DUID-1";
    private const string OwnId = "AprZAARz4r";
    private const string RenamedId = "KUW3HHjMUR";

    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-ids-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Package()
    {
        var path = Path.Combine(_dir, "app.wgt");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("config.xml").Open()))
            writer.Write($"""
                <?xml version="1.0" encoding="UTF-8"?>
                <widget xmlns="http://www.w3.org/ns/widgets" xmlns:tizen="http://tizen.org/ns/widgets" id="http://example.org/app" version="2.1.0">
                    <tizen:application id="{OwnId}.Jellyfin" package="{OwnId}" required_version="4.0"/>
                </widget>
                """);
        zip.CreateEntry("index.html");
        return path;
    }

    private static string Remembered() => PackageIdRenameStore.Set(null, new PackageIdRenameRecord
    {
        TvDuid = Tv,
        OriginalPackageId = OwnId,
        NewPackageId = RenamedId,
        OriginalAppId = $"{OwnId}.Jellyfin",
        NewAppId = $"{RenamedId}.Jellyfin",
    });

    [Fact]
    public async Task RememberedRename_AppliesWithoutBeingAsked()
    {
        var config = RenamesConfig.With(Remembered());
        var wgt = Package();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv), config);

        Assert.NotNull(rename);
        Assert.Equal(RenamedId, rename!.NewId);
        Assert.Equal(($"{RenamedId}.Jellyfin", RenamedId), await WgtManifest.ReadIdsAsync(wgt));
        Assert.NotNull(PackageIdRenameStore.Find(config.PackageIdRenamesJson, Tv, OwnId));
    }

    // Deleting the app on the TV and reinstalling brought the remembered id straight back (a tester's
    // TV-Store). OwnId is the way out: the record goes, the package keeps its own id.
    [Fact]
    public async Task OwnId_ForgetsTheRename_AndKeepsThePackagesOwnId()
    {
        var config = RenamesConfig.With(Remembered());
        var wgt = Package();
        var progress = new List<string>();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, OwnId: true), config, progress.Add);

        Assert.Null(rename);
        Assert.Equal(($"{OwnId}.Jellyfin", OwnId), await WgtManifest.ReadIdsAsync(wgt));
        Assert.Null(PackageIdRenameStore.Find(config.PackageIdRenamesJson, Tv, OwnId));
        Assert.Contains(progress, line => line.Contains(RenamedId, StringComparison.Ordinal) && line.Contains(OwnId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnId_LeavesOtherTvsRenames()
    {
        var json = PackageIdRenameStore.Set(Remembered(), new PackageIdRenameRecord { TvDuid = "DUID-2", OriginalPackageId = OwnId, NewPackageId = "ZZZZZZZZZZ" });
        var config = RenamesConfig.With(json);

        await PackageIds.ApplyAsync(Package(), new PackageIdPolicy(Tv, OwnId: true), config);

        Assert.Null(PackageIdRenameStore.Find(config.PackageIdRenamesJson, Tv, OwnId));
        Assert.Equal("ZZZZZZZZZZ", PackageIdRenameStore.Find(config.PackageIdRenamesJson, "DUID-2", OwnId)?.NewPackageId);
    }

    [Fact]
    public async Task OwnId_WithNothingRemembered_IsANoOp()
    {
        var config = RenamesConfig.With(string.Empty);
        var wgt = Package();

        Assert.Null(await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, OwnId: true), config));
        Assert.Equal(OwnId, (await WgtManifest.ReadIdsAsync(wgt)).PackageId);
        Assert.Equal(string.Empty, config.PackageIdRenamesJson);
    }

    // Both at once: not the remembered id, a fresh one, remembered in its turn.
    [Fact]
    public async Task OwnId_AndNewId_GiveAFreshId()
    {
        var config = RenamesConfig.With(Remembered());
        var wgt = Package();

        var rename = await PackageIds.ApplyAsync(wgt, new PackageIdPolicy(Tv, NewId: true, OwnId: true), config);

        Assert.NotNull(rename);
        Assert.NotEqual(RenamedId, rename!.NewId);
        Assert.NotEqual(OwnId, rename.NewId);
        Assert.Equal(rename.NewId, PackageIdRenameStore.Find(config.PackageIdRenamesJson, Tv, OwnId)?.NewPackageId);
    }

    [Fact]
    public async Task NoPolicy_LeavesThePackageAlone()
    {
        var config = RenamesConfig.With(Remembered());
        var wgt = Package();

        Assert.Null(await PackageIds.ApplyAsync(wgt, null, config));
        Assert.Equal(OwnId, (await WgtManifest.ReadIdsAsync(wgt)).PackageId);
    }

    // IAppConfig is wide; ApplyAsync reads and writes only PackageIdRenamesJson.
    public class RenamesConfig : DispatchProxy
    {
        private string _json = string.Empty;

        public static IAppConfig With(string json)
        {
            var config = Create<IAppConfig, RenamesConfig>();
            ((RenamesConfig)(object)config)._json = json;
            return config;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_" + nameof(IAppConfig.PackageIdRenamesJson))
                return _json;
            if (method?.Name == "set_" + nameof(IAppConfig.PackageIdRenamesJson))
            {
                _json = args?[0] as string ?? string.Empty;
                return null;
            }
            var type = method?.ReturnType;
            return type == typeof(string) ? string.Empty
                : type is { IsValueType: true } && type != typeof(void) ? Activator.CreateInstance(type)
                : null;
        }
    }
}
