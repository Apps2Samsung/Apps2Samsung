using System.IO.Compression;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

public class TvStoreAddressPatcherTests
{
    private const string Config = "<widget><tizen:application id=\"A2STVStore.Store\" package=\"A2STVStore\" required_version=\"4.0\"/>"
        + "<tizen:content-security-policy>default-src *; connect-src * http://127.0.0.1:8077</tizen:content-security-policy></widget>";
    private const string AppJs = "    var BASE = 'http://127.0.0.1:8077';\n";

    private static string MakeWgt(string config)
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2s-tvstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wgt = Path.Combine(dir, "TV-Store.wgt");
        using (var zip = ZipFile.Open(wgt, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("config.xml").Open())) w.Write(config);
            using (var w = new StreamWriter(zip.CreateEntry("js/app.js").Open())) w.Write(AppJs);
        }
        return wgt;
    }

    private static string Read(string wgt, string name)
    {
        using var zip = ZipFile.OpenRead(wgt);
        using var r = new StreamReader(zip.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public async Task Writes_the_tv_ip_into_config_and_app()
    {
        var wgt = MakeWgt(Config);
        var patcher = new TvStoreAddressPatcher();
        Assert.True(patcher.CanHandle(wgt));

        using (var ws = PackageWorkspace.Extract(wgt, "203.0.113.7"))
        {
            await patcher.ApplyAsync(ws);
            ws.RepackIfChanged();
        }

        Assert.Contains("connect-src * http://203.0.113.7:8077", Read(wgt, "config.xml"));
        Assert.Equal("    var BASE = 'http://203.0.113.7:8077';\n", Read(wgt, "js/app.js"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("not-an-ip")]
    public async Task Leaves_loopback_without_a_usable_ip(string? ip)
    {
        var wgt = MakeWgt(Config);
        using (var ws = PackageWorkspace.Extract(wgt, ip))
        {
            await new TvStoreAddressPatcher().ApplyAsync(ws);
            ws.RepackIfChanged();
        }
        Assert.Equal(AppJs, Read(wgt, "js/app.js"));
    }

    [Fact]
    public void Ignores_other_packages() =>
        Assert.False(new TvStoreAddressPatcher().CanHandle(MakeWgt(Config.Replace("A2STVStore", "VetroTV001"))));
}
