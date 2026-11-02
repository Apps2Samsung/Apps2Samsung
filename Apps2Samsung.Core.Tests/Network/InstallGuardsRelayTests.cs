using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;

namespace Apps2Samsung.Tests.Network;

/// <summary>
/// The pre-install guards and the not-ready verdict for a TV whose Developer Mode points at
/// 127.0.0.1 (TV-Store#52): that is the relay setting, not a mismatch with this device, and the
/// one thing to warn about is a relay port that does not answer.
/// </summary>
public class InstallGuardsRelayTests
{
    private static readonly InstallGuardOptions ThisPc = new()
    {
        LocalIps = new[] { "192.168.1.20" },
        ConfiguredLocalIp = "192.168.1.20",
    };

    private static NetworkDevice Tv(string developerIp, bool? relayPortOpen = null) => new()
    {
        IpAddress = "192.168.1.50",
        DeveloperMode = "1",
        DeveloperIP = developerIp,
        DebugPortOpen = true,
        RelayPortOpen = relayPortOpen,
    };

    [Fact]
    public void LoopbackWithTheRelayAnsweringIsNoGuardAtAll()
    {
        var result = InstallGuards.Evaluate(Tv("127.0.0.1", relayPortOpen: true), ThisPc, new SubnetsDiffer());
        Assert.Empty(result.Guards);
    }

    [Fact]
    public void LoopbackWithTheRelayUnprobedIsNoGuardEither()
    {
        var result = InstallGuards.Evaluate(Tv("127.0.0.1"), ThisPc, new SubnetsDiffer());
        Assert.Empty(result.Guards);
    }

    [Fact]
    public void ARelayThatSaysWhyItIsNotListeningPutsThatInTheGuardDetail()
    {
        var tv = Tv("127.0.0.1", relayPortOpen: false);
        tv.RelayError = "port 26201 is already in use";

        var guard = Assert.Single(InstallGuards.Evaluate(tv, ThisPc, new SubnetsDiffer()).Guards);

        Assert.Equal(InstallGuardKind.RelayNotAnswering, guard.Kind);
        Assert.Contains("port 26201 is already in use", guard.Detail);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"relay\":{\"enabled\":true,\"listening\":false,\"error\":\"EADDRINUSE 26201\"}}", "EADDRINUSE 26201")]
    [InlineData("{\"relay\":{\"enabled\":false,\"listening\":false,\"error\":\"EADDRINUSE 26201\"}}", null)]
    [InlineData("{\"relay\":{\"enabled\":true,\"listening\":true,\"error\":null}}", null)]
    [InlineData("{\"relay\":{\"enabled\":true,\"listening\":false,\"error\":null}}", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void OnlyASwitchOnRelayThatIsNotListeningHasABindError(string? body, string? expected)
    {
        Assert.Equal(expected, Apps2Samsung.Sdb.SdbEndpoint.ParseRelayBindError(body));
    }

    [Fact]
    public void LoopbackWithTheRelayDownIsOneActionableGuard()
    {
        var result = InstallGuards.Evaluate(Tv("127.0.0.1", relayPortOpen: false), ThisPc, new SubnetsDiffer());
        var guard = Assert.Single(result.Guards);
        Assert.Equal(InstallGuardKind.RelayNotAnswering, guard.Kind);
        Assert.Equal("guardRelayTitle", guard.TitleKey);
        Assert.Equal("guardRelayBody", guard.MessageKey);
        Assert.Contains("TV-Store", guard.DefaultMessage);
        Assert.Equal("TV-Store relay: 192.168.1.50:26201", guard.Detail);
        Assert.Null(result.CorrectedTvIp);
    }

    [Fact]
    public void AnotherMachinesAddressIsStillAMismatch()
    {
        var result = InstallGuards.Evaluate(Tv("192.168.1.99"), ThisPc, new SameSubnet());
        Assert.Contains(result.Guards, g => g.Kind == InstallGuardKind.DeveloperIpMismatch);
        Assert.DoesNotContain(result.Guards, g => g.Kind == InstallGuardKind.RelayNotAnswering);
    }

    [Fact]
    public void NotReadyWithLoopbackIsTheTvsOwnServiceNotAMismatch()
    {
        var tv = Tv("127.0.0.1");
        tv.DebugPortOpen = false;
        Assert.Equal(TizenDeviceReadiness.NotReadyReason.PowerCycle, TizenDeviceReadiness.WhyNotReady(tv, "192.168.1.20"));

        var other = Tv("192.168.1.99");
        other.DebugPortOpen = false;
        Assert.Equal(TizenDeviceReadiness.NotReadyReason.DeveloperIpMismatch, TizenDeviceReadiness.WhyNotReady(other, "192.168.1.20"));
    }

    [Fact]
    public void TheInformationViewSaysWhichWayTheConnectionGoes()
    {
        var viaRelay = new TizenDeviceInfo("192.168.1.50", "", "", "", "", "", "", "1", "127.0.0.1", true);
        Assert.Contains(viaRelay.Rows, r => r.Label == "Connects via" && r.Value.StartsWith("TV-Store relay, port 26201", StringComparison.Ordinal));

        var direct = new TizenDeviceInfo("192.168.1.50", "", "", "", "", "", "", "1", "192.168.1.20", true);
        Assert.Contains(direct.Rows, r => r.Label == "Connects via" && r.Value == "sdbd directly, port 26101");
    }

    /// <summary>A network where every pair of addresses is on a different subnet: the subnet guard
    /// would fire for 127.0.0.1 if the relay setting did not switch it off.</summary>
    private class SubnetsDiffer : INetworkService
    {
        public virtual bool IsDifferentSubnet(string ip1, string ip2) => true;
        public string InvertIPAddress(string ipAddress) => string.Join(".", ipAddress.Split('.').Reverse());
        public Task<IEnumerable<NetworkDevice>> GetLocalTizenAddresses(CancellationToken cancellationToken = default, bool virtualScan = false) => throw new NotSupportedException();
        public Task<NetworkDevice?> ValidateManualTizenAddress(string ip, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IEnumerable<IPAddress> GetRelevantLocalIPs(bool virtualScan = false) => throw new NotSupportedException();
        public Task<bool> IsPortOpenAsync(string ip, int port, CancellationToken ct) => throw new NotSupportedException();
        public string? GetLocalIPAddress() => "192.168.1.20";
        public string? GetLocalIpOnSubnetOf(string? remoteIp) => "192.168.1.20";
        public Task<IReadOnlyList<NetworkInterfaceOption>> GetNetworkInterfaceOptionsAsync() => throw new NotSupportedException();
        public string? GetActiveVpnAdapterName() => null;
        public bool LastScanBlockedByOs => false;
    }

    private sealed class SameSubnet : SubnetsDiffer
    {
        public override bool IsDifferentSubnet(string ip1, string ip2) => false;
    }
}
