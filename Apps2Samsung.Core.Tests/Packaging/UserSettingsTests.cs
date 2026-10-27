using System.IO.Compression;
using System.Reflection;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Tests.Packaging;

public class UserSettingsTests
{
    // Vetro's port: optional, defaulted, ranged, and written into three files.
    private static UserSettingsSpec PortSpec() => new()
    {
        Fields =
        {
            new UserSettingsField
            {
                Key = "port", Label = "Dashboard port", Type = "number", Required = false, Default = "8099",
                Min = 1024, Max = 65535, Disallow = new List<long> { 8001, 8002, 26101 },
                Targets = new List<UserSettingsTarget>
                {
                    new() { File = "config.xml", Find = "http://127.0.0.1:8099\"", Replace = "http://127.0.0.1:{value}\"" },
                    new() { File = "index.html", Find = "var BASE = 'http://127.0.0.1:8099';", Replace = "var BASE = 'http://127.0.0.1:{value}';" },
                    new() { File = "index.html", Find = "did not answer on port 8099.", Replace = "did not answer on port {value}." },
                    new() { File = "service/main.js", Find = "|| buildInfo.port || 8099;", Replace = "|| buildInfo.port || {value};" },
                },
            },
        },
    };

    private static UserSettingsSpec SingleFileSpec() => new()
    {
        File = "dashboard-url.js",
        Fields =
        {
            new UserSettingsField
            {
                Key = "dashboard_url", Label = "Dashboard URL", Type = "text",
                Find = "window.DASHBOARD_URL = \"\";", Replace = "window.DASHBOARD_URL = \"{value}\";",
            },
        },
    };

    [Theory]
    [InlineData("9000", null)]
    [InlineData("1024", null)]
    [InlineData("65535", null)]
    [InlineData("", null)]
    [InlineData("80", "userSettingRange")]
    [InlineData("70000", "userSettingRange")]
    [InlineData("99999999999999999999", "userSettingRange")]
    [InlineData("8001", "userSettingNotAllowed")]
    [InlineData("8002", "userSettingNotAllowed")]
    [InlineData("26101", "userSettingNotAllowed")]
    [InlineData("80a", "userSettingDigitsOnly")]
    public void Validate_Port(string value, string? expected)
    {
        Assert.Equal(expected, UserSettingsStore.Validate(PortSpec().Fields[0], value));
    }

    [Fact]
    public void Validate_OnlyMinOrMax_PicksItsOwnMessage()
    {
        Assert.Equal("userSettingMin", UserSettingsStore.Validate(new UserSettingsField { Type = "number", Min = 10 }, "5"));
        Assert.Equal("userSettingMax", UserSettingsStore.Validate(new UserSettingsField { Type = "number", Max = 10 }, "11"));
    }

    [Fact]
    public void Message_FillsInMinAndMax()
    {
        var text = UserSettingsStore.Message(PortSpec().Fields[0], "userSettingRange", _ => "Between {0} and {1}");
        Assert.Equal("Between 1024 and 65535", text);
    }

    [Fact]
    public void Set_EmptyOptionalField_TakesTheDefault_ButDoesNotPrefillIt()
    {
        var json = UserSettingsStore.Set(null, "Vetro.wgt", PortSpec(), new Dictionary<string, string> { ["port"] = "" });

        var patch = UserSettingsStore.Get(json, "Vetro.wgt")!;
        Assert.Equal("8099", Assert.Single(patch.Fields).Value);
        Assert.Empty(patch.Values);
        Assert.Equal(4, patch.Edits().Count());
    }

    [Fact]
    public void Set_TypedValue_IsPrefilledNextTime()
    {
        var json = UserSettingsStore.Set(null, "Vetro.wgt", PortSpec(), new Dictionary<string, string> { ["port"] = "9000" });

        Assert.Equal("9000", UserSettingsStore.Get(json, "Vetro.wgt")!.Values["port"]);
    }

    [Fact]
    public void StoredSingleFileEntry_FromBeforeTargets_StillReadsAsOneFile()
    {
        const string json = "{\"Frame-Dashboard.wgt\":{\"file\":\"dashboard-url.js\",\"fields\":[{\"key\":\"dashboard_url\",\"type\":\"text\",\"find\":\"a\",\"replace\":\"b{value}\",\"value\":\"https://x\"}]}}";

        var edit = Assert.Single(UserSettingsStore.Get(json, "Frame-Dashboard.wgt")!.Edits());
        Assert.Equal("dashboard-url.js", edit.File);
        Assert.Equal("https://x", edit.Value);
    }

    [Fact]
    public async Task Patcher_WritesEveryTarget()
    {
        using var dir = new TempDir();
        var package = dir.Package("Vetro.wgt", new()
        {
            ["config.xml"] = "<access origin=\"http://127.0.0.1:8099\" subdomains=\"false\"/>",
            ["index.html"] = "var BASE = 'http://127.0.0.1:8099';\n<p>did not answer on port 8099.</p>",
            ["service/main.js"] = "const port = Number(process.env.VETRO_PORT) || buildInfo.port || 8099;",
        });
        var config = FakeConfig.With(UserSettingsStore.Set(null, "Vetro.wgt", PortSpec(), new Dictionary<string, string> { ["port"] = "9000" }));

        using (var ws = PackageWorkspace.Extract(package))
        {
            await new UserSettingsPatcher(config).ApplyAsync(ws);
            Assert.Contains("http://127.0.0.1:9000\"", File.ReadAllText(Path.Combine(ws.Root, "config.xml")));
            var index = File.ReadAllText(Path.Combine(ws.Root, "index.html"));
            Assert.Contains("var BASE = 'http://127.0.0.1:9000';", index);
            Assert.Contains("did not answer on port 9000.", index);
            Assert.Contains("|| buildInfo.port || 9000;", File.ReadAllText(Path.Combine(ws.Root, "service", "main.js")));
            Assert.DoesNotContain("8099", index);
        }
    }

    [Fact]
    public async Task Patcher_MissingFind_NamesTheFileItWasLookingIn()
    {
        using var dir = new TempDir();
        var package = dir.Package("Vetro.wgt", new()
        {
            ["config.xml"] = "<access origin=\"http://127.0.0.1:8099\" subdomains=\"false\"/>",
            ["index.html"] = "var BASE = 'http://127.0.0.1:8099';\n<p>did not answer on port 8099.</p>",
            ["service/main.js"] = "const port = 8099;",
        });
        var config = FakeConfig.With(UserSettingsStore.Set(null, "Vetro.wgt", PortSpec(), new Dictionary<string, string>()));

        using var ws = PackageWorkspace.Extract(package);
        var ex = await Assert.ThrowsAsync<UserSettingsPatchException>(() => new UserSettingsPatcher(config).ApplyAsync(ws));
        Assert.Equal("service/main.js", ex.File);
        Assert.Equal("|| buildInfo.port || 8099;", ex.Find);
    }

    [Fact]
    public async Task Patcher_SingleFileForm_WorksAsBefore()
    {
        using var dir = new TempDir();
        var package = dir.Package("Frame-Dashboard.wgt", new() { ["dashboard-url.js"] = "window.DASHBOARD_URL = \"\";\n" });
        var config = FakeConfig.With(UserSettingsStore.Set(null, "Frame-Dashboard.wgt", SingleFileSpec(),
            new Dictionary<string, string> { ["dashboard_url"] = "https://x/?k=\"1\"" }));

        using var ws = PackageWorkspace.Extract(package);
        await new UserSettingsPatcher(config).ApplyAsync(ws);
        Assert.Equal("window.DASHBOARD_URL = \"https://x/?k=\\\"1\\\"\";\n", File.ReadAllText(Path.Combine(ws.Root, "dashboard-url.js")));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("a2s-usersettings-").FullName;

        public string Package(string name, Dictionary<string, string> files)
        {
            var path = System.IO.Path.Combine(Path, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (entry, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(entry).Open()))
                    w.Write(text);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }

    // IAppConfig is wide; the patcher reads only AppUserSettingsJson, everything else is its default.
    public class FakeConfig : DispatchProxy
    {
        private string _json = string.Empty;

        public static IAppConfig With(string json)
        {
            var config = Create<IAppConfig, FakeConfig>();
            ((FakeConfig)(object)config)._json = json;
            return config;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_" + nameof(IAppConfig.AppUserSettingsJson))
                return _json;
            var type = method?.ReturnType;
            return type == typeof(string) ? string.Empty
                : type is { IsValueType: true } && type != typeof(void) ? Activator.CreateInstance(type)
                : null;
        }
    }
}
