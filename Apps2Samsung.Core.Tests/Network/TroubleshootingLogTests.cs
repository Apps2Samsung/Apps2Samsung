using System.Text.Json.Nodes;
using Apps2Samsung.Services;

namespace Apps2Samsung.Core.Tests.Network;

// What the debug log gets about the network (#1012): the TV's /api/v2/ device object and the
// address this device reaches the TV from.
public sealed class TroubleshootingLogTests
{
    [Fact]
    public void DescribeDevice_KeepsTheFields_AndHidesTheWifiName()
    {
        var device = JsonNode.Parse("""
            {"developerIP":"192.168.11.2","developerMode":"1","modelName":"QA55QN95CAJXZK","ssid":"Home WiFi","wifiMac":"aa:bb:cc:dd:ee:ff"}
            """)!;

        var line = TizenDeveloperInfo.DescribeDevice(device);

        Assert.Contains("\"developerIP\":\"192.168.11.2\"", line);
        Assert.Contains("\"developerMode\":\"1\"", line);
        Assert.Contains("\"modelName\":\"QA55QN95CAJXZK\"", line);
        Assert.Contains("\"ssid\":\"(hidden)\"", line);
        Assert.DoesNotContain("Home WiFi", line);
        Assert.Equal("Home WiFi", device["ssid"]!.GetValue<string>());     // the answer itself is untouched
    }

    [Fact]
    public void DescribeDevice_WithoutSsid_AddsNone()
    {
        Assert.Equal("""{"developerMode":"0"}""", TizenDeveloperInfo.DescribeDevice(JsonNode.Parse("""{"developerMode":"0"}""")!));
    }

    [Fact]
    public void Toward_Loopback_IsLoopback_AndJunkIsNull()
    {
        Assert.Equal("127.0.0.1", LocalAddress.Toward("127.0.0.1"));
        Assert.Null(LocalAddress.Toward("not an ip"));
        Assert.Null(LocalAddress.Toward(null));
    }
}
