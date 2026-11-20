using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class TizenAppDebuggerTests
{
    [Theory]
    // Tizen 3.0 (2017) and older only answer the suffixed form; the bare one gets the channel closed (#954).
    [InlineData("3.0", true)]
    [InlineData("2.4", true)]
    [InlineData("3.0.0", true)]
    // From 4.0 on the bare form is the one Tizen Studio sends.
    [InlineData("4.0", false)]
    [InlineData("5.0", false)]
    [InlineData("10.0", false)]
    public void UsesLegacyDebugForm_FollowsTheTizenVersion(string platformVersion, bool expected)
    {
        Assert.Equal(expected, TizenAppDebugger.UsesLegacyDebugForm(Version.Parse(platformVersion)));
    }

    [Fact]
    public void UsesLegacyDebugForm_UnknownVersion_StartsWithTheBareForm()
    {
        Assert.False(TizenAppDebugger.UsesLegacyDebugForm(null));
    }

    [Fact]
    public void DebugCommand_BareForm_IsTheVerbAndTheId()
    {
        Assert.Equal("0 debug A2STVStore.Store", TizenAppDebugger.DebugCommand("A2STVStore.Store", legacyForm: false));
    }

    [Fact]
    public void DebugCommand_LegacyForm_CarriesTheWait()
    {
        Assert.Equal($"0 debug A2STVStore.Store {TizenAppDebugger.LegacyDebugWait}", TizenAppDebugger.DebugCommand("A2STVStore.Store", legacyForm: true));
    }

    // The TV takes one developer connection at a time (#1140): the open session's tunnel has to be
    // down before the service is stopped and launched, and back up afterwards.
    [Fact]
    public async Task StartAlongside_ReleasesTheOpenTunnelAroundTheCommands()
    {
        var sdb = new RecordingSdb();
        var app = await TizenAppDebugger.StartAsync(sdb, "10.114.0.1", "Pkg.App", localPort: 41001);
        sdb.Calls.Clear();

        await using var service = await TizenAppDebugger.StartAlongsideAsync(sdb, "10.114.0.1", "Pkg.Service", app, localPort: 41002);

        Assert.Equal(
            new[]
            {
                "close 41001>5001",
                "shell 0 was_kill Pkg.Service",
                "shell 0 debug Pkg.Service",
                "forward 41002>5002",
                "forward 41001>5001",
            },
            sdb.Calls);
        Assert.False(app.IsSuspended);
        Assert.Equal(5002, service.RemotePort);
    }

    [Fact]
    public async Task StartAlongside_GivesTheTunnelBackWhenTheServiceDoesNotStart()
    {
        var sdb = new RecordingSdb();
        var app = await TizenAppDebugger.StartAsync(sdb, "10.114.0.2", "Pkg.App", localPort: 41003);
        sdb.Calls.Clear();
        sdb.Refuse = "Pkg.Missing";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TizenAppDebugger.StartAlongsideAsync(sdb, "10.114.0.2", "Pkg.Missing", app, localPort: 41004));

        Assert.Equal("close 41003>5001", sdb.Calls[0]);
        Assert.Equal("forward 41003>5001", sdb.Calls[^1]);
        Assert.DoesNotContain(sdb.Calls, call => call.StartsWith("forward 41004", StringComparison.Ordinal));
        Assert.False(app.IsSuspended);
    }

    [Fact]
    public async Task ADisposedSessionDoesNotComeBack()
    {
        var sdb = new RecordingSdb();
        var app = await TizenAppDebugger.StartAsync(sdb, "10.114.0.3", "Pkg.App", localPort: 41005);
        await app.SuspendAsync();
        await app.DisposeAsync();
        sdb.Calls.Clear();

        await app.ResumeAsync();

        Assert.Empty(sdb.Calls);
        Assert.True(app.IsSuspended);
    }

    // Answers the stop and the debug launch, hands out an inspector port per id, and writes down
    // every command and every tunnel that opens or closes, in order.
    private sealed class RecordingSdb : ISdbEngine
    {
        private int _nextPort = 5001;

        public List<string> Calls { get; } = new();
        public string? Refuse { get; set; }

        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command)
        {
            Calls.Add($"shell {command}");
            if (!command.StartsWith("0 debug ", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult { ExitCode = 0, Output = string.Empty, Error = string.Empty });

            return Task.FromResult(Refuse is not null && command.EndsWith(Refuse, StringComparison.Ordinal)
                ? new ProcessResult { ExitCode = 1, Output = "no such application", Error = "no such application" }
                : new ProcessResult { ExitCode = 0, Output = $"... successfully launched pid = 12 with debug 1 port: {_nextPort++}", Error = string.Empty });
        }

        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort)
        {
            Calls.Add($"forward {localPort}>{remotePort}");
            return Task.FromResult<IAsyncDisposable>(new Tunnel(() => Calls.Add($"close {localPort}>{remotePort}")));
        }

        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) =>
            Task.FromResult(new ProcessResult { ExitCode = 0, Output = string.Empty, Error = string.Empty });

        // No capability report: the version stays unknown and the bare `0 debug` form is sent.
        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => throw new NotSupportedException();

        public Task<ProcessResult> AppsAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DuidAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => throw new NotSupportedException();
        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ExecuteAsync(string tvIpAddress, string appId) => throw new NotSupportedException();
        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass) => throw new NotSupportedException();
        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath, IProgress<int>? progress = null) => throw new NotSupportedException();
        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId) => throw new NotSupportedException();
        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => throw new NotSupportedException();

        private sealed class Tunnel(Action closed) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                closed();
                return ValueTask.CompletedTask;
            }
        }
    }
}
