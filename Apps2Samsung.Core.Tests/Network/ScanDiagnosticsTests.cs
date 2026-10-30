using System;
using Apps2Samsung.Models;
using Apps2Samsung.Services;

namespace Apps2Samsung.Tests.Network;

/// <summary>
/// The per-scan diagnostics line (#922): everything the readiness verdict was computed from, so a
/// "not ready after idle" report can be read off the debug log without a reproduction.
/// </summary>
public class ScanDiagnosticsTests
{
    [Fact]
    public void NamesTheLocalSideAndEveryTv()
    {
        var line = ScanDiagnostics.Describe(
            configuredLocalIp: "192.168.1.10",
            defaultRouteIp: "192.168.1.10",
            localIps: new[] { "192.168.1.10", "10.8.0.2" },
            devices: new[]
            {
                new NetworkDevice { IpAddress = "192.168.1.50", DeveloperMode = "1", DeveloperIP = "192.168.1.10", DebugPortOpen = true },
                new NetworkDevice { IpAddress = "192.168.1.51", DeveloperMode = "1", DeveloperIP = "192.168.1.99", DebugPortOpen = false },
            });

        Assert.Equal(
            "[Scan] local side: configured=192.168.1.10 default-route=192.168.1.10 interfaces=[192.168.1.10, 10.8.0.2]"
            + "; TV 192.168.1.50: developerMode=1 developerIP=192.168.1.10 debugPort=open verdict=Ready"
            + "; TV 192.168.1.51: developerMode=1 developerIP=192.168.1.99 debugPort=closed verdict=DeveloperIpMismatch",
            line);
    }

    [Fact]
    public void SaysWhenTheLocalSideIsUnknownAndNothingWasFound()
    {
        var line = ScanDiagnostics.Describe(null, null, Array.Empty<string>(), Array.Empty<NetworkDevice>());

        Assert.Equal("[Scan] local side: configured=(none) default-route=(none) interfaces=[]; no TV found", line);
    }

    [Fact]
    public void VerdictSkipsTheIpComparisonWithoutAConfiguredIp()
    {
        // The stale-after-wake case: no configured address means no mismatch can be claimed.
        var line = ScanDiagnostics.Describe(
            configuredLocalIp: "",
            defaultRouteIp: null,
            localIps: Array.Empty<string>(),
            devices: new[]
            {
                new NetworkDevice { IpAddress = "192.168.1.51", DeveloperMode = "1", DeveloperIP = "192.168.1.99", DebugPortOpen = false },
            });

        Assert.Contains("verdict=PowerCycle", line);
        Assert.DoesNotContain("127.0.0.1", line);
    }
}
