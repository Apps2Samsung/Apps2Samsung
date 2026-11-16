using System.IO.Compression;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

// Every Jellyfin build declares 0.1.0 in config.xml; the jellyfin-web version is in the main bundle (#1113).
public class JellyfinWebVersionTests
{
    // The expression as it stands in the 2026-11-15 builds (Jellyfin.wgt and Jellyfin-master.wgt).
    private const string Before = "void 0!==e&&e.appVersion?window.NativeShell.AppHost.appVersion():\"";
    private const string After = "\"},getPushTokenInfo:function(){return{}}";

    private static string Package(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"a2s-webversion-{Guid.NewGuid():N}.wgt");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    [Theory]
    [InlineData("12.4.0")]
    [InlineData("13.0.0")]
    [InlineData("10.11.11")]
    public void ReadsTheVersionFromTheMainBundle(string version)
    {
        var path = Package(
            ("config.xml", "<widget version=\"0.1.0\"/>"),
            ("www/main.jellyfin.bundle.js", "var a=1;" + Before + version + After));
        try { Assert.Equal(version, JellyfinWebVersion.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ABundleUnderAnotherMainName_IsReadToo()
    {
        var path = Package(("www/main.bundle.js", Before + "10.8.13" + After));
        try { Assert.Equal("10.8.13", JellyfinWebVersion.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OtherFilesAreNotSearched()
    {
        // The api client's own version strings and a bundle in a subfolder say nothing about the build.
        var path = Package(
            ("www/node_modules.jellyfin-apiclient.bundle.js", "u._minServerVersion=\"3.2.33\";" + Before + "9.9.9" + After),
            ("www/libraries/main.js", Before + "9.9.9" + After),
            ("tizen.js", "appVersion: tizen.application.getCurrentApplication().appInfo.version"));
        try { Assert.Null(JellyfinWebVersion.Read(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NotAPackage_IsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"a2s-webversion-{Guid.NewGuid():N}.wgt");
        File.WriteAllText(path, "not a zip");
        try { Assert.Null(JellyfinWebVersion.Read(path)); }
        finally { File.Delete(path); }
    }
}
