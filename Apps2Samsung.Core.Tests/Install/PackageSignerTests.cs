using System.IO.Compression;
using System.Text;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Core.Tests.Install;

public sealed class PackageSignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "a2s-sign-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly SigningProfile _profile;

    public PackageSignerTests()
    {
        Directory.CreateDirectory(_dir);
        _input = Path.Combine(_dir, "app.wgt");
        using (var zip = ZipFile.Open(_input, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("config.xml");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write("""<?xml version="1.0" encoding="UTF-8"?><widget xmlns="http://www.w3.org/ns/widgets" xmlns:tizen="http://tizen.org/ns/widgets" id="http://example.org/app" version="1.0.0"><tizen:application id="Pkg1234567.App" package="Pkg1234567" required_version="4.0"/></widget>""");
        }
        // The validity check reads the files when they exist and reports "unreadable" when they do
        // not; unreadable does not block, so the signer runs with paths that name nothing.
        var profileDir = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(profileDir);
        _profile = new SigningProfile(Path.Combine(profileDir, "author.p12"), Path.Combine(profileDir, "distributor.p12"), "pw", profileDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Signs_a_copy_and_leaves_the_input_alone()
    {
        var before = await File.ReadAllBytesAsync(_input);
        var output = Path.Combine(_dir, "out", "app-signed.wgt");
        var sdb = new MarkingSdb();
        var keys = new List<string>();

        var signed = await PackageSigner.SignCopyAsync(sdb, _input, output, _profile, keys.Add);

        Assert.Equal(Path.GetFullPath(output), signed.Path);
        Assert.Equal("Pkg1234567.App", signed.AppId);
        Assert.Equal("Pkg1234567", signed.PackageId);
        Assert.Equal(before, await File.ReadAllBytesAsync(_input));
        Assert.EndsWith(MarkingSdb.Marker, await File.ReadAllTextAsync(output, Encoding.Latin1));
        Assert.NotEqual(_input, sdb.SignedPath);
        Assert.Contains("statusSigningPackage", keys);
        // The staged copy the signer worked on is gone.
        Assert.False(File.Exists(sdb.SignedPath));
    }

    [Fact]
    public async Task Refuses_the_input_as_the_output()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PackageSigner.SignCopyAsync(new MarkingSdb(), _input, _input, _profile));
    }

    [Fact]
    public async Task Refuses_a_file_that_is_not_a_package()
    {
        var text = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(text, "x");
        await Assert.ThrowsAsync<ArgumentException>(() => PackageSigner.SignCopyAsync(new MarkingSdb(), text, Path.Combine(_dir, "o.wgt"), _profile));
    }

    [Fact]
    public async Task A_signer_failure_says_what_it_printed()
    {
        var output = Path.Combine(_dir, "o.wgt");
        var ex = await Assert.ThrowsAsync<PackageSignException>(() => PackageSigner.SignCopyAsync(new FailingSdb(), _input, output, _profile));
        Assert.Contains("no such certificate", ex.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Suggests_a_signed_name_next_to_the_input()
    {
        Assert.Equal("app-signed.wgt", PackageSigner.SuggestedOutputName("/x/app.wgt"));
        Assert.Equal("Thing-signed.tpk", PackageSigner.SuggestedOutputName("Thing.tpk"));
        Assert.True(PackageSigner.IsPackage("a.WGT"));
        Assert.False(PackageSigner.IsPackage("a.zip"));
    }

    /// <summary>"Signs" by appending a marker to the file in place, as the real signer rewrites it.</summary>
    private class MarkingSdb : ISdbEngine
    {
        public const string Marker = "<signed>";
        public string? SignedPath { get; private set; }

        public virtual Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
        {
            SignedPath = packagePath;
            File.AppendAllText(packagePath, Marker, Encoding.Latin1);
            return Task.FromResult(new ProcessResult { ExitCode = 0, Output = "signed" });
        }

        public Task<ProcessResult> AppsAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DuidAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ExecuteAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath, IProgress<int>? progress = null) => throw new NotSupportedException();
        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId) => throw new NotSupportedException();
        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => throw new NotSupportedException();
        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command) => throw new NotSupportedException();
        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort) => throw new NotSupportedException();
    }

    private sealed class FailingSdb : MarkingSdb
    {
        public override Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass) =>
            Task.FromResult(new ProcessResult { ExitCode = 1, Error = "no such certificate" });
    }
}
