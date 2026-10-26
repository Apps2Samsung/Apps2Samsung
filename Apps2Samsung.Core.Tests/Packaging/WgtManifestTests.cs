using System.IO.Compression;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

public sealed class WgtManifestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-wgt-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Package(string entryName, string content, string fileName = "app.wgt")
    {
        var path = Path.Combine(_dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(entryName).Open()))
            writer.Write(content);
        zip.CreateEntry("index.html");
        return path;
    }

    private const string Config = """
        <?xml version="1.0" encoding="UTF-8"?>
        <widget xmlns="http://www.w3.org/ns/widgets" xmlns:tizen="http://tizen.org/ns/widgets" id="http://example.org/app" version="2.1.0">
            <tizen:application id="AprZAARz4r.Jellyfin" package="AprZAARz4r" required_version="4.0"/>
            <tizen:privilege name="http://tizen.org/privilege/internet"/>
        </widget>
        """;

    [Fact]
    public async Task ReadIds_FromConfigXml()
    {
        var (appId, packageId) = await WgtManifest.ReadIdsAsync(Package("config.xml", Config));

        Assert.Equal("AprZAARz4r.Jellyfin", appId);
        Assert.Equal("AprZAARz4r", packageId);
    }

    [Fact]
    public async Task ReadIds_NoPackageAttribute_TakesTheIdPrefix()
    {
        var config = Config.Replace(" package=\"AprZAARz4r\"", "");

        var (_, packageId) = await WgtManifest.ReadIdsAsync(Package("config.xml", config));

        Assert.Equal("AprZAARz4r", packageId);
    }

    [Fact]
    public async Task ReadIds_MalformedXml_FallsBackToTheText()
    {
        // An unescaped ampersand: not well-formed, which some third-party packagers produce.
        var config = Config.Replace("<tizen:privilege", "<name>Tom & Jerry</name><tizen:privilege");

        var (appId, packageId) = await WgtManifest.ReadIdsAsync(Package("config.xml", config));

        Assert.Equal("AprZAARz4r.Jellyfin", appId);
        Assert.Equal("AprZAARz4r", packageId);
    }

    [Fact]
    public async Task ReadIds_NotAPackage_IsNulls()
    {
        var path = Path.Combine(_dir, "broken.wgt");
        await File.WriteAllTextAsync(path, "not a zip");

        Assert.Equal((null, null), await WgtManifest.ReadIdsAsync(path));
        Assert.Equal((null, null), await WgtManifest.ReadIdsAsync(Path.Combine(_dir, "missing.wgt")));
    }

    [Fact]
    public async Task ReadRequiredVersion()
    {
        Assert.Equal("4.0", await WgtManifest.ReadRequiredVersionAsync(Package("config.xml", Config)));
        Assert.Null(await WgtManifest.ReadRequiredVersionAsync(Package("config.xml", Config.Replace(" required_version=\"4.0\"", ""), "plain.wgt")));
    }

    [Fact]
    public async Task ReadPackageInfo_Widget()
    {
        var info = await WgtManifest.ReadPackageInfoAsync(Package("config.xml", Config));

        Assert.Equal(new WgtPackageInfo("2.1.0", "AprZAARz4r.Jellyfin", "AprZAARz4r"), info);
    }

    [Fact]
    public async Task ReadPackageInfo_NativeTpk()
    {
        const string manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <manifest xmlns="http://tizen.org/ns/packages" api-version="6.0" package="org.example.native" version="1.4.2">
                <ui-application appid="org.example.native.main" exec="native" type="capp"/>
            </manifest>
            """;

        var info = await WgtManifest.ReadPackageInfoAsync(Package("tizen-manifest.xml", manifest, "native.tpk"));

        Assert.Equal(new WgtPackageInfo("1.4.2", "org.example.native.main", "org.example.native"), info);
    }

    [Theory]
    [InlineData("5.5", "6.0", true)]
    [InlineData("6.5", "6.0", false)]
    [InlineData("6.0", "6.0", false)]
    // Unknown or unreadable versions never block an install.
    [InlineData(null, "6.0", false)]
    [InlineData("6.5", "latest", false)]
    [InlineData("6.5", null, false)]
    public void RequiresNewerTizen(string? tv, string? required, bool expected)
    {
        var tvVersion = tv is null ? null : Version.Parse(tv);

        Assert.Equal(expected, WgtManifest.RequiresNewerTizen(tvVersion, required));
    }
}
