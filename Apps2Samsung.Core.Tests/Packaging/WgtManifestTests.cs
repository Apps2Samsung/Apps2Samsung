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

    /// <summary>A package with a manifest and any number of binary entries (icons).</summary>
    private string PackageWith(string fileName, string manifestName, string manifest, params (string Name, byte[] Bytes)[] files)
    {
        var path = Path.Combine(_dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(manifestName).Open()))
            writer.Write(manifest);
        foreach (var (name, bytes) in files)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(bytes);
        }
        return path;
    }

    // The signatures WgtManifest.ImageExtension and TV-Store's icons.js both recognise, with a few
    // bytes of "image" behind them.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0];
    private static readonly byte[] WebP = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 1];

    private static string WidgetConfig(string iconElement) => Config.Replace("<tizen:application", iconElement + "\n    <tizen:application");

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

        Assert.Equal(new WgtPackageInfo("2.1.0", "AprZAARz4r.Jellyfin", "AprZAARz4r", "4.0"), info);
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

        Assert.Equal(new WgtPackageInfo("1.4.2", "org.example.native.main", "org.example.native", "6.0"), info);
    }

    [Fact]
    public async Task ReadPackageInfo_NoRequiredVersion_IsNull()
    {
        var info = await WgtManifest.ReadPackageInfoAsync(Package("config.xml", Config.Replace(" required_version=\"4.0\"", ""), "plain.wgt"));

        Assert.Null(info.RequiredVersion);
        Assert.Equal("AprZAARz4r", info.PackageId);
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

    [Fact]
    public async Task ReadIcon_Widget_FirstIconFromConfigXml()
    {
        var path = PackageWith("icon.wgt", "config.xml", WidgetConfig("<icon src=\"icon.png\"/><icon src=\"other.png\"/>"),
            ("icon.png", Png), ("other.png", Jpeg));

        var icon = await WgtManifest.ReadIconAsync(path);

        Assert.NotNull(icon);
        Assert.Equal("png", icon.Extension);
        Assert.Equal(Png, icon.Bytes);
    }

    [Fact]
    public async Task ReadIcon_Widget_PathSpellingDoesNotMatter()
    {
        // "./images/Icon.PNG" in the manifest, images/icon.png in the zip: the same file.
        var path = PackageWith("icon.wgt", "config.xml", WidgetConfig("<icon src=\"./images/Icon.PNG\"/>"),
            ("images/icon.png", WebP));

        var icon = await WgtManifest.ReadIconAsync(path);

        Assert.NotNull(icon);
        Assert.Equal("webp", icon.Extension);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("gif")]
    public async Task ReadIcon_TypeComesFromTheBytes_NotTheName(string kind)
    {
        // A .png that is really a JPEG or a GIF is published under what it is.
        var bytes = kind == "jpg" ? Jpeg : Gif;
        var path = PackageWith("icon.wgt", "config.xml", WidgetConfig("<icon src=\"icon.png\"/>"), ("icon.png", bytes));

        var icon = await WgtManifest.ReadIconAsync(path);

        Assert.Equal(kind, icon?.Extension);
    }

    [Fact]
    public async Task ReadIcon_Svg_IsNull()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var path = PackageWith("icon.wgt", "config.xml", WidgetConfig("<icon src=\"icon.svg\"/>"), ("icon.svg", svg));

        Assert.Null(await WgtManifest.ReadIconAsync(path));
    }

    [Fact]
    public async Task ReadIcon_MissingFileOrNoIconElement_IsNull()
    {
        Assert.Null(await WgtManifest.ReadIconAsync(PackageWith("gone.wgt", "config.xml", WidgetConfig("<icon src=\"icon.png\"/>"))));
        Assert.Null(await WgtManifest.ReadIconAsync(PackageWith("none.wgt", "config.xml", Config, ("icon.png", Png))));
        Assert.Null(await WgtManifest.ReadIconAsync(Package("index.html", "<html/>", "nomanifest.wgt")));
        Assert.Null(await WgtManifest.ReadIconAsync(Path.Combine(_dir, "missing.wgt")));
    }

    private const string NativeManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <manifest xmlns="http://tizen.org/ns/packages" api-version="6.0" package="org.example.native" version="1.4.2">
            <ui-application appid="org.example.native.main" exec="native" type="capp">
                <label>Native</label>
                <icon>native.png</icon>
            </ui-application>
        </manifest>
        """;

    [Theory]
    [InlineData("shared/res/native.png")]
    [InlineData("native.png")]
    [InlineData("res/native.png")]
    public async Task ReadIcon_NativeTpk_WhereverItShips(string entry)
    {
        var path = PackageWith("native.tpk", "tizen-manifest.xml", NativeManifest, (entry, Jpeg));

        var icon = await WgtManifest.ReadIconAsync(path);

        Assert.NotNull(icon);
        Assert.Equal("jpg", icon.Extension);
        Assert.Equal(Jpeg, icon.Bytes);
    }

    [Fact]
    public async Task ReadIcon_NativeTpk_PathInManifest_FindsTheBareName()
    {
        var manifest = NativeManifest.Replace("<icon>native.png</icon>", "<icon>/opt/usr/apps/org.example.native/shared/res/native.png</icon>");
        var path = PackageWith("native.tpk", "tizen-manifest.xml", manifest, ("shared/res/native.png", Png));

        Assert.Equal("png", (await WgtManifest.ReadIconAsync(path))?.Extension);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, "jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x3E }, null)]
    [InlineData(new byte[] { 0x89, 0x50 }, null)]
    [InlineData(new byte[0], null)]
    public void ImageExtension_FromTheSignature(byte[] bytes, string? expected)
    {
        Assert.Equal(expected, WgtManifest.ImageExtension(bytes));
    }
}
