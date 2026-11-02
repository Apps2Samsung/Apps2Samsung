using Apps2Samsung.Design;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Design;

/// <summary>
/// The Developer Mode guide for a TV whose Host PC IP is 127.0.0.1 (TV-Store#52, #962): that is the
/// relay setting, never a mismatch with this device. What can be wrong is the relay being off.
/// </summary>
public class DevModeGuideRelayTests
{
    private static readonly string[] ThisPc = { "192.168.1.20" };

    private static NetworkDevice Tv(string developerIp, bool? relayPortOpen = null) => new()
    {
        IpAddress = "192.168.1.50",
        DeveloperMode = "1",
        DeveloperIP = developerIp,
        DebugPortOpen = true,
        RelayPortOpen = relayPortOpen,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void LoopbackIsNotAMismatch(bool? relayPortOpen)
    {
        Assert.Equal(DevModeProblem.None, DevModeGuide.Diagnose(Tv("127.0.0.1", relayPortOpen), ThisPc));
    }

    [Fact]
    public void LoopbackWithTheRelayOffIsTheRelayProblem()
    {
        Assert.Equal(DevModeProblem.RelayDown, DevModeGuide.Diagnose(Tv("127.0.0.1", relayPortOpen: false), ThisPc));
    }

    [Fact]
    public void AnotherMachineIsStillAMismatch()
    {
        Assert.Equal(DevModeProblem.IpMismatch, DevModeGuide.Diagnose(Tv("192.168.1.99"), ThisPc));
    }

    [Fact]
    public void RelayStatusSaysSoInsteadOfTheSetupSteps()
    {
        var device = Tv("127.0.0.1", relayPortOpen: false);
        Assert.Equal("devGuideTitleRelay", DevModeGuide.TitleKey(DevModeProblem.RelayDown));
        Assert.Equal("devGuideStatusRelayDown", DevModeGuide.Status(DevModeProblem.RelayDown, device, "192.168.1.20", key => key));
        Assert.Equal("devGuideStatusReadyRelay", DevModeGuide.Status(DevModeProblem.None, Tv("127.0.0.1", true), "192.168.1.20", key => key));
        Assert.Equal("devGuideStatusReady", DevModeGuide.Status(DevModeProblem.None, Tv("192.168.1.20"), "192.168.1.20", key => key));

        var steps = DevModeGuide.Steps(DevModeProblem.RelayDown);
        Assert.All(steps, step => Assert.True(step.Needed));
        Assert.All(steps, step => Assert.StartsWith("devGuideRelayStep", step.TitleKey));
        Assert.DoesNotContain(steps, step => step.ShowsLocalIp);
    }
}
