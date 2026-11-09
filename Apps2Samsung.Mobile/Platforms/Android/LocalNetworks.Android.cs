using System.Net;
using Apps2Samsung.Models;
using JavaNetworkInterface = Java.Net.NetworkInterface;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Android: <c>java.net.NetworkInterface.getNetworkInterfaces()</c>, which, unlike the .NET
/// enumeration on this platform, reports every up interface with its IPv4 addresses and prefix
/// lengths. Readable without location permission; the hotspot interface shows up like any other.
/// </summary>
public static partial class LocalNetworks
{
    // A home LAN wider than a /24 would sweep thousands of hosts (the Core scan narrows such a
    // range to the /24 of the *network* address, which on a 192.168.0.0/16 LAN is the wrong block
    // for a phone at 192.168.1.x). Clamp to the /24 around the phone's own address instead: that
    // is what the single-seed scan always covered, so the home case keeps its scan time.
    private const int MinimumPrefixLength = 24;

    private static partial IEnumerable<LocalNetwork> EnumeratePlatform()
    {
        var found = new List<LocalNetwork>();
        var interfaces = JavaNetworkInterface.NetworkInterfaces;
        if (interfaces is null)
            return found;

        while (interfaces.HasMoreElements)
        {
            if (interfaces.NextElement() is not JavaNetworkInterface ni)
                continue;

            if (!ni.IsUp || ni.IsLoopback || ni.InterfaceAddresses is not { } addresses)
                continue;

            foreach (var ia in addresses)
            {
                var bytes = ia.Address?.GetAddress();
                if (bytes is not { Length: 4 })
                    continue;

                var prefix = Math.Max((int)ia.NetworkPrefixLength, MinimumPrefixLength);
                found.Add(new LocalNetwork(new IPAddress(bytes), LocalNetwork.MaskFromPrefixLength(prefix), ni.Name));
            }
        }

        return found;
    }
}
