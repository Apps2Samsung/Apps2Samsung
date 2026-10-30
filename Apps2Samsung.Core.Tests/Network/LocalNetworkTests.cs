using System.Net;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Network;

/// <summary>
/// The pure parts of the multi-network scan seed (#920): which local network a TV is on, which
/// interfaces are a hotspot or cellular, and the mask arithmetic the Android enumerator relies on.
/// </summary>
public class LocalNetworkTests
{
    private static LocalNetwork Net(string address, int prefix, string? name = null) =>
        new(IPAddress.Parse(address), LocalNetwork.MaskFromPrefixLength(prefix), name);

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(25, "255.255.255.128")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(1, "128.0.0.0")]
    public void MaskFromPrefixLength(int prefix, string expected) =>
        Assert.Equal(IPAddress.Parse(expected), LocalNetwork.MaskFromPrefixLength(prefix));

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(-1)]
    public void OutOfRangePrefixFallsBackToSlash24(int prefix) =>
        Assert.Equal(LocalNetwork.DefaultMask, LocalNetwork.MaskFromPrefixLength(prefix));

    [Theory]
    [InlineData("192.168.43.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.1.2", false)]   // CGNAT: the carrier side of a phone
    [InlineData("169.254.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivateAddress(string address, bool expected) =>
        Assert.Equal(expected, LocalNetwork.IsPrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public void ContainsRespectsTheMask()
    {
        var hotspot = Net("192.168.43.1", 24, "swlan0");

        Assert.True(hotspot.Contains(IPAddress.Parse("192.168.43.17")));
        Assert.False(hotspot.Contains(IPAddress.Parse("192.168.1.17")));
        Assert.Equal(IPAddress.Parse("192.168.43.0"), hotspot.NetworkAddress);
    }

    [Fact]
    public void LocalIpForPicksTheNetworkTheTvIsOn()
    {
        // Phone on home Wi-Fi and hosting a hotspot at the same time (Wi-Fi sharing).
        var networks = new[]
        {
            Net("192.168.1.20", 24, "wlan0"),
            Net("192.168.43.1", 24, "swlan0"),
        };

        Assert.Equal("192.168.43.1", LocalNetwork.LocalIpFor(networks, "192.168.43.55"));
        Assert.Equal("192.168.1.20", LocalNetwork.LocalIpFor(networks, "192.168.1.8"));
        Assert.Null(LocalNetwork.LocalIpFor(networks, "10.1.2.3"));
        Assert.Null(LocalNetwork.LocalIpFor(networks, "not an ip"));
        Assert.Null(LocalNetwork.LocalIpFor(networks, null));
    }

    [Theory]
    [InlineData("swlan0", true)]
    [InlineData("ap0", true)]
    [InlineData("softap0", true)]
    [InlineData("wlan1", true)]
    [InlineData("wlan0", false)]
    [InlineData("rmnet_data0", false)]
    [InlineData(null, false)]
    public void HotspotInterfacesAreRecognisedByName(string? name, bool expected) =>
        Assert.Equal(expected, Net("192.168.43.1", 24, name).IsHotspot);

    [Theory]
    [InlineData("rmnet_data0", true)]
    [InlineData("rmnet0", true)]
    [InlineData("ccmni0", true)]
    [InlineData("clat4", true)]
    [InlineData("tun0", true)]
    [InlineData("v4-rmnet_data0", true)]
    [InlineData("wlan0", false)]
    [InlineData("swlan0", false)]
    [InlineData("eth0", false)]
    public void CellularAndTunnelInterfacesAreRecognisedByName(string name, bool expected) =>
        Assert.Equal(expected, Net("10.20.30.40", 24, name).IsWan);
}
