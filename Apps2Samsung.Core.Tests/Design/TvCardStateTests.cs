using Apps2Samsung.Design;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Design;

/// <summary>
/// The TV card says what the Developer Mode guide knows: a TV whose Host PC IP points elsewhere is
/// amber even with the debug port open, a relay TV says so, and the chips lead to the fix.
/// </summary>
public class TvCardStateTests
{
    private static readonly string[] ThisDevice = { "192.168.1.33" };
    private static string Key(string key) => key; // the localizer returns the key, so the asserts name keys

    private static NetworkDevice Tv(string? developerIp = "192.168.1.33", string mode = "1", bool debugPort = true, bool? relayPort = null) => new()
    {
        IpAddress = "192.168.1.50",
        DeviceName = "Woonkamer",
        ModelName = "UE55",
        DeveloperMode = mode,
        DeveloperIP = developerIp,
        DebugPortOpen = debugPort,
        RelayPortOpen = relayPort,
    };

    [Fact]
    public void ReadyDirect()
    {
        var state = TvCardStates.ForDevice(Tv(), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Ok, state.Led);
        Assert.Equal("tvCardReady", state.Message);
        Assert.Equal(TvRoute.Direct, state.Route);
        Assert.False(state.ViaRelay);
        Assert.Empty(state.Actions);
        Assert.Equal("Woonkamer", state.Name);
        Assert.Equal("UE55 · 192.168.1.50", state.Subline);
        Assert.Equal(string.Empty, TvCardStates.RouteText(state, Key));
        Assert.True(state.IsDevice);
    }

    [Fact]
    public void WrongHostPcIpIsAmberEvenWithThePortOpen()
    {
        var state = TvCardStates.ForDevice(Tv(developerIp: "192.168.1.20"), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Warn, state.Led);
        Assert.Equal(DevModeProblem.IpMismatch, state.Problem);
        Assert.Equal("devGuideStatusIpMismatch", state.Message); // string.Format of the key leaves the key
        Assert.Equal(new[] { TvCardAction.FixDeveloperMode }, state.Actions);
    }

    [Fact]
    public void ReversedIpIsNamed()
    {
        var state = TvCardStates.ForDevice(Tv(developerIp: "33.1.168.192"), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(DevModeProblem.IpReversed, state.Problem);
        Assert.Equal("devGuideStatusIpReversed", state.Message);
        Assert.Equal(new[] { TvCardAction.FixDeveloperMode }, state.Actions);
    }

    [Fact]
    public void NoLocalAddressesSkipsTheComparisonLikeTheGuide()
    {
        var state = TvCardStates.ForDevice(Tv(developerIp: "192.168.1.20"), Array.Empty<string>(), null, Key);
        Assert.Equal(TvLed.Ok, state.Led);
    }

    [Fact]
    public void TheNamedAddressCountsAsLocal()
    {
        // A phone on its hotspot: the interface list misses the hotspot address the TV points at (#920).
        var state = TvCardStates.ForDevice(Tv(developerIp: "192.168.43.1"), new[] { "10.0.0.5" }, "192.168.43.1", Key);
        Assert.Equal(TvLed.Ok, state.Led);
    }

    [Fact]
    public void DeveloperModeOff()
    {
        var state = TvCardStates.ForDevice(Tv(mode: "0"), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Warn, state.Led);
        Assert.Equal("devGuideStatusOff", state.Message);
        Assert.Equal(new[] { TvCardAction.FixDeveloperMode }, state.Actions);
    }

    [Fact]
    public void RestartRequired()
    {
        var state = TvCardStates.ForDevice(Tv(debugPort: false), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Warn, state.Led);
        Assert.Equal("devGuideStatusRestart", state.Message);
        Assert.Equal(new[] { TvCardAction.Restarted }, state.Actions);
    }

    [Fact]
    public void RelayReady()
    {
        var state = TvCardStates.ForDevice(Tv(developerIp: "127.0.0.1", relayPort: true), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Ok, state.Led);
        Assert.Equal("tvCardReadyRelay", state.Message);
        Assert.Equal(TvRoute.Relay, state.Route);
        Assert.True(state.ViaRelay);
        Assert.False(state.RelayDown);
        Assert.Equal("tvCardViaRelay", TvCardStates.RouteText(state, Key));
        Assert.Empty(state.Actions);
    }

    [Fact]
    public void RelayDownIsNotARestart()
    {
        var state = TvCardStates.ForDevice(Tv(developerIp: "127.0.0.1", debugPort: false, relayPort: false), ThisDevice, "192.168.1.33", Key);
        Assert.Equal(TvLed.Warn, state.Led);
        Assert.Equal(DevModeProblem.RelayDown, state.Problem);
        Assert.Equal("tvCardRelayDown", state.Message);
        Assert.True(state.RelayDown);
        Assert.Equal("tvCardRelayOff", TvCardStates.RouteText(state, Key));
        Assert.Equal(new[] { TvCardAction.TurnOnRelay, TvCardAction.CheckAgain }, state.Actions);
    }

    [Fact]
    public void RowStatusIsShort()
    {
        Assert.Equal((TvLed.Ok, ""), TvCardStates.RowStatus(Tv(), ThisDevice, null, Key));
        Assert.Equal((TvLed.Ok, "tvRowViaRelay"), TvCardStates.RowStatus(Tv(developerIp: "127.0.0.1", relayPort: true), ThisDevice, null, Key));
        Assert.Equal((TvLed.Warn, "tvRowRelayOff"), TvCardStates.RowStatus(Tv(developerIp: "127.0.0.1", relayPort: false), ThisDevice, null, Key));
        Assert.Equal((TvLed.Warn, "tvRowPointsAt"), TvCardStates.RowStatus(Tv(developerIp: "192.168.1.20"), ThisDevice, null, Key));
        Assert.Equal((TvLed.Warn, "tvRowRestart"), TvCardStates.RowStatus(Tv(debugPort: false), ThisDevice, null, Key));
        Assert.Equal((TvLed.Warn, "tvRowDevModeOff"), TvCardStates.RowStatus(Tv(mode: "0"), ThisDevice, null, Key));
    }

    [Fact]
    public void EveryActionHasALabelKey()
    {
        foreach (var action in Enum.GetValues<TvCardAction>())
            Assert.StartsWith("tvCardAction", TvCardStates.ActionLabelKey(action));
    }
}
