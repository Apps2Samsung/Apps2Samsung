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
/// The Advanced "Skip Developer Mode checks" setting (#1026): it drops the restart and
/// Developer-Mode-off guards so a tester sees where the install really fails, and nothing else.
/// </summary>
public class InstallGuardsSkipDevModeTests
{
    private static InstallGuardOptions Options(bool skip) => new()
    {
        LocalIps = new[] { "192.168.1.20" },
        ConfiguredLocalIp = "192.168.1.20",
        SkipDevModeChecks = skip,
    };

    // A TV that says Developer Mode off and whose install port didn't answer.
    private static NetworkDevice NotReadyTv(string developerIp = "192.168.1.20", bool? relayPortOpen = null) => new()
    {
        IpAddress = "192.168.1.50",
        DeveloperMode = "0",
        DeveloperIP = developerIp,
        DebugPortOpen = false,
        RelayPortOpen = relayPortOpen,
    };

    [Fact]
    public void ByDefaultBothDeveloperModeGuardsFire()
    {
        var kinds = InstallGuards.Evaluate(NotReadyTv(), Options(skip: false), new Net(sameSubnet: true)).Guards.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { InstallGuardKind.RestartRequired, InstallGuardKind.DeveloperModeOff }, kinds);
    }

    [Fact]
    public void SkippingDropsTheRestartAndDeveloperModeOffGuards()
    {
        var result = InstallGuards.Evaluate(NotReadyTv(), Options(skip: true), new Net(sameSubnet: true));
        Assert.Empty(result.Guards);
    }

    [Fact]
    public void SkippingKeepsTheIpMismatchGuard()
    {
        var kinds = InstallGuards.Evaluate(NotReadyTv("192.168.1.99"), Options(skip: true), new Net(sameSubnet: true)).Guards.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { InstallGuardKind.DeveloperIpMismatch }, kinds);
    }

    [Fact]
    public void SkippingKeepsTheReversedIpGuard()
    {
        var kinds = InstallGuards.Evaluate(NotReadyTv("20.1.168.192"), Options(skip: true), new Net(sameSubnet: true)).Guards.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { InstallGuardKind.DeveloperIpReversed }, kinds);
    }

    [Fact]
    public void SkippingKeepsTheSubnetGuard()
    {
        var kinds = InstallGuards.Evaluate(NotReadyTv(), Options(skip: true), new Net(sameSubnet: false)).Guards.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { InstallGuardKind.SubnetMismatch }, kinds);
    }

    [Fact]
    public void SkippingKeepsTheRelayGuard()
    {
        var kinds = InstallGuards.Evaluate(NotReadyTv("127.0.0.1", relayPortOpen: false), Options(skip: true), new Net(sameSubnet: true)).Guards.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { InstallGuardKind.RelayNotAnswering }, kinds);
    }

    private sealed class Net(bool sameSubnet) : INetworkService
    {
        public bool IsDifferentSubnet(string ip1, string ip2) => !sameSubnet;
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
}

/// <summary>The TV card with Skip Developer Mode checks on (#1026): the reminder, no chip for the
/// skipped check, and the TV's own verdict left as it is.</summary>
public class TvCardSkipDevModeTests
{
    private static string L(string key) => key;

    private static NetworkDevice Tv(string developerMode, string developerIp, bool portOpen) => new()
    {
        IpAddress = "192.168.1.50",
        DeveloperMode = developerMode,
        DeveloperIP = developerIp,
        DebugPortOpen = portOpen,
    };

    private static Apps2Samsung.Design.TvCardState Card(NetworkDevice tv, bool skip) =>
        Apps2Samsung.Design.TvCardStates.WithDevModeChecksSkipped(
            Apps2Samsung.Design.TvCardStates.ForDevice(tv, new[] { "192.168.1.20" }, "192.168.1.20", L), skip, L);

    [Fact]
    public void OffLeavesTheCardAsItIs()
    {
        var card = Card(Tv("0", "192.168.1.20", false), skip: false);
        Assert.False(card.HasNote);
        Assert.Contains(Apps2Samsung.Design.TvCardAction.FixDeveloperMode, card.Actions);
    }

    [Fact]
    public void DeveloperModeOffKeepsItsLineButLosesTheFixChip()
    {
        var card = Card(Tv("0", "192.168.1.20", false), skip: true);
        Assert.Equal("tvCardDevModeChecksOff", card.Note);
        Assert.Equal("devGuideStatusOff", card.Message);
        Assert.Equal(Apps2Samsung.Design.TvLed.Warn, card.Led);
        Assert.Empty(card.Actions);
    }

    [Fact]
    public void ARestartLosesItsChipToo()
    {
        var card = Card(Tv("1", "192.168.1.20", false), skip: true);
        Assert.Equal(Apps2Samsung.Design.DevModeProblem.RestartRequired, card.Problem);
        Assert.Empty(card.Actions);
    }

    [Fact]
    public void AnIpMismatchKeepsItsChipAsThatGuardStillAsks()
    {
        var card = Card(Tv("1", "192.168.1.99", true), skip: true);
        Assert.Equal(Apps2Samsung.Design.DevModeProblem.IpMismatch, card.Problem);
        Assert.Contains(Apps2Samsung.Design.TvCardAction.FixDeveloperMode, card.Actions);
        Assert.True(card.HasNote);
    }

    [Fact]
    public void NoTvCardGetsNoNote()
    {
        var card = Apps2Samsung.Design.TvCardStates.WithDevModeChecksSkipped(
            Apps2Samsung.Design.TvCardStates.NoTv(L, "192.168.1.20"), true, L);
        Assert.False(card.HasNote);
    }
}
