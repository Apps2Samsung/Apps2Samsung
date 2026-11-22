using System.IO.Compression;
using System.Reflection;
using Apps2Samsung.Configuration;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Install;

// PackageInstaller's recovery after the TV said no (#1147), against a TV that answers from a script.
public sealed class PackageInstallerRecoveryTests : IDisposable
{
    private const string Tv = "192.0.2.77";
    private const string Duid = "DUID-1";
    private const string OwnId = "AprZAARz4r";

    private const string Ok = "app_id[Pkg.App] installing[100]\ninstall completed";
    private const string Mismatch = "install failed[118, -12], reason: Check certificate error : :Package with the same id and different certificate has been already installed";
    private const string Blocked = "app_id[Pkg.App] install failed[118, -22]";
    private const string Conflict = "app_id[Pkg.App] install failed[118]\ncmd_ret:0";
    private const string CannotUninstall = "Testing '0 vd_appuninstall test': FAILED";

    private static readonly SigningProfile Current = new("current-author.p12", "current-dist.p12", "pw", "current");
    private static readonly SigningProfile Earlier = new("earlier-author.p12", "earlier-dist.p12", "pw", "earlier");

    private readonly string _dir = Directory.CreateTempSubdirectory("a2s-recover-").FullName;

    public PackageInstallerRecoveryTests() => SdbEndpoint.Remember(Tv, "192.0.2.10");

    public void Dispose()
    {
        SdbEndpoint.Forget(Tv);
        Directory.Delete(_dir, recursive: true);
    }

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

    private static PackageInstaller Installer(ScriptedTv tv, IAppConfig config) =>
        new(tv, config, Array.Empty<IPackagePatcher>());

    [Fact]
    public async Task An_earlier_certificate_is_tried_before_anything_is_removed()
    {
        var tv = new ScriptedTv(Mismatch, Ok);
        bool? told = null;

        var output = await Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, Package(), Current,
            alternativeCertificate: _ => Task.FromResult<AlternativeCertificate?>(
                new AlternativeCertificate(Earlier, helped => { told = helped; return Task.CompletedTask; })));

        Assert.Equal(Ok, output);
        Assert.Equal(new[] { Current.AuthorP12, Earlier.AuthorP12 }, tv.SignedWith);
        Assert.Empty(tv.Uninstalled);
        Assert.True(told);
    }

    [Fact]
    public async Task An_earlier_certificate_that_does_not_help_is_put_back_before_the_removal()
    {
        var tv = new ScriptedTv(Mismatch, Mismatch, Ok);
        bool? told = null;
        InstallFailure? askedFor = null;

        var output = await Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, Package(), Current,
            confirmRemove: () => Task.FromResult(true),
            alternativeCertificate: failure =>
            {
                askedFor = failure;
                return Task.FromResult<AlternativeCertificate?>(
                    new AlternativeCertificate(Earlier, helped => { told = helped; return Task.CompletedTask; }));
            });

        Assert.Equal(Ok, output);
        Assert.Equal(InstallFailure.CertificateMismatch, askedFor);
        Assert.False(told);
        // Signed for the install, with the earlier one, and back with the current one for the retry.
        Assert.Equal(new[] { Current.AuthorP12, Earlier.AuthorP12, Current.AuthorP12 }, tv.SignedWith);
        Assert.Equal(new[] { OwnId }, tv.Uninstalled);
    }

    [Fact]
    public async Task A_tv_that_cannot_remove_apps_is_not_asked_to()
    {
        var tv = new ScriptedTv(Mismatch) { Diagnose = CannotUninstall };
        bool asked = false;

        var ex = await Assert.ThrowsAsync<InstallFailedException>(() =>
            Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, Package(), Current,
                confirmRemove: () => { asked = true; return Task.FromResult(true); }));

        Assert.Equal(InstallFailure.CertificateMismatchTvCannotRemove, ex.Failure);
        Assert.False(asked);
        Assert.Empty(tv.Uninstalled);
        Assert.Equal(1, tv.Pushes);
    }

    [Fact]
    public async Task A_blocked_id_on_a_tv_that_cannot_remove_apps_says_so_without_asking()
    {
        var tv = new ScriptedTv(Blocked) { Diagnose = CannotUninstall };

        var ex = await Assert.ThrowsAsync<InstallFailedException>(() =>
            Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, Package(), Current,
                confirmRemove: () => throw new InvalidOperationException("must not be asked")));

        Assert.Equal(InstallFailure.PackageIdBlocked, ex.Failure);
    }

    [Fact]
    public async Task An_id_conflict_that_survives_the_removal_gets_a_new_id_that_is_remembered()
    {
        var tv = new ScriptedTv(Conflict, Conflict, Ok);
        var config = Settings.With(overwrite: true);
        var wgt = Package();
        PackageIdRename? told = null;

        var output = await Installer(tv, config).InstallAsync(Tv, wgt, Current,
            confirmRemove: () => Task.FromResult(true),
            packageId: new PackageIdPolicy(Duid),
            renamed: rename => told = rename);

        Assert.Equal(Ok, output);
        var (_, idNow) = await WgtManifest.ReadIdsAsync(wgt);
        Assert.NotEqual(OwnId, idNow);
        Assert.Equal(idNow, told?.NewId);
        Assert.Equal(idNow, PackageIdRenameStore.Find(config.PackageIdRenamesJson, Duid, OwnId)?.NewPackageId);
        // The renamed package was signed again before it went out.
        Assert.Equal(new[] { Current.AuthorP12, Current.AuthorP12 }, tv.SignedWith);
        Assert.Equal(3, tv.Pushes);
    }

    [Fact]
    public async Task An_id_conflict_keeps_the_packages_own_id_when_that_was_asked_for()
    {
        var tv = new ScriptedTv(Conflict, Conflict);
        var wgt = Package();

        var ex = await Assert.ThrowsAsync<InstallFailedException>(() =>
            Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, wgt, Current,
                confirmRemove: () => Task.FromResult(true),
                packageId: new PackageIdPolicy(Duid, OwnId: true)));

        Assert.Equal(InstallFailure.Generic, ex.Failure);
        Assert.Equal(OwnId, (await WgtManifest.ReadIdsAsync(wgt)).Item2);
    }

    [Fact]
    public async Task Removal_is_still_asked_and_a_no_leaves_the_tv_alone()
    {
        var tv = new ScriptedTv(Mismatch);

        await Assert.ThrowsAsync<RemovalDeclinedException>(() =>
            Installer(tv, Settings.With(overwrite: true)).InstallAsync(Tv, Package(), Current,
                confirmRemove: () => Task.FromResult(false)));

        Assert.Empty(tv.Uninstalled);
    }

    [Fact]
    public async Task With_overwrite_off_the_desktop_reading_stops_before_the_push()
    {
        var tv = new ScriptedTv(Ok) { Apps = OwnId };

        var ex = await Assert.ThrowsAsync<InstallFailedException>(() =>
            Installer(tv, Settings.With(overwrite: false)).InstallAsync(Tv, Package(), Current,
                keepInstalledWhenOverwriteOff: true));

        Assert.Equal(InstallFailure.InstalledAndRemovalOff, ex.Failure);
        Assert.Equal(0, tv.Pushes);
        Assert.Empty(tv.SignedWith);
    }

    [Fact]
    public async Task With_overwrite_off_and_a_tv_that_cannot_remove_the_text_says_delete_it_on_the_tv()
    {
        var tv = new ScriptedTv(Ok) { Apps = OwnId, Diagnose = CannotUninstall };

        var ex = await Assert.ThrowsAsync<InstallFailedException>(() =>
            Installer(tv, Settings.With(overwrite: false, deletePrevious: true)).InstallAsync(Tv, Package(), Current,
                keepInstalledWhenOverwriteOff: true));

        Assert.Equal(InstallFailure.InstalledAndTvCannotRemove, ex.Failure);
        Assert.Equal(0, tv.Pushes);
    }

    [Fact]
    public async Task With_overwrite_off_the_phone_and_the_cli_still_update_in_place()
    {
        var tv = new ScriptedTv(Ok) { Apps = OwnId };

        var output = await Installer(tv, Settings.With(overwrite: false)).InstallAsync(Tv, Package(), Current);

        Assert.Equal(Ok, output);
        Assert.Equal(1, tv.Pushes);
    }

    /// <summary>A TV that answers each push with the next line of its script and records what it was told.</summary>
    private sealed class ScriptedTv(params string[] pushAnswers) : ISdbEngine
    {
        private readonly Queue<string> _answers = new(pushAnswers);

        public string Diagnose { get; init; } = "Testing '0 vd_appuninstall test': OK";
        public string Apps { get; init; } = string.Empty;
        public List<string> SignedWith { get; } = new();
        public List<string> Uninstalled { get; } = new();
        public int Pushes { get; private set; }

        private static Task<ProcessResult> Reply(string output) =>
            Task.FromResult(new ProcessResult { ExitCode = 0, Output = output });

        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) =>
            Reply("platform_version:6.0\nsdk_toolpath:/home/owner/share/tmp/sdk_tools");

        public Task<ProcessResult> AppsAsync(string tvIpAddress) => Reply(Apps);
        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => Reply(Diagnose);

        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
        {
            SignedWith.Add(authorP12);
            return Reply("signed");
        }

        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath, IProgress<int>? progress = null)
        {
            Pushes++;
            return Reply(_answers.Dequeue());
        }

        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId)
        {
            Uninstalled.Add(packageId);
            return Reply("uninstalled");
        }

        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DuidAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ExecuteAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => throw new NotSupportedException();
        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command) => throw new NotSupportedException();
        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort) => throw new NotSupportedException();
    }

    // IAppConfig is wide; the installer's recovery reads three switches and the rename store.
    public class Settings : DispatchProxy
    {
        private readonly Dictionary<string, object?> _values = new();

        public static IAppConfig With(bool overwrite, bool deletePrevious = false)
        {
            var config = Create<IAppConfig, Settings>();
            var self = (Settings)(object)config;
            self._values[nameof(IAppConfig.TryOverwrite)] = overwrite;
            self._values[nameof(IAppConfig.DeletePreviousInstall)] = deletePrevious;
            self._values[nameof(IAppConfig.PackageIdRenamesJson)] = string.Empty;
            return config;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method?.Name ?? string.Empty;
            if (name.StartsWith("set_", StringComparison.Ordinal))
            {
                _values[name[4..]] = args?[0];
                return null;
            }
            if (name.StartsWith("get_", StringComparison.Ordinal) && _values.TryGetValue(name[4..], out var value))
                return value;
            var type = method?.ReturnType;
            return type == typeof(string) ? string.Empty
                : type is { IsValueType: true } && type != typeof(void) ? Activator.CreateInstance(type)
                : null;
        }
    }
}
