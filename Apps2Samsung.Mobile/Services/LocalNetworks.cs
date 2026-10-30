using System.Diagnostics;
using System.Net;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The LAN-side networks this phone is on, read through the platform's own interface API.
/// <para>
/// .NET's <c>NetworkInterface</c> is unusable for this on Android (the adapter type is never
/// Ethernet/Wireless80211 and <c>IPv4Mask</c> throws), which is why the scanner used to be fed
/// one seed address: the local end of a socket towards the internet. With the phone hosting a
/// Wi-Fi hotspot that seed is the <em>cellular</em> address, so the hotspot subnet, the one the TV
/// is on, was never swept (#920). <c>Platforms/Android</c> walks
/// <c>java.net.NetworkInterface</c> instead, which sees every interface with a real mask: Wi-Fi,
/// the hotspot (<c>swlan0</c>/<c>ap0</c>), and both at once when Wi-Fi sharing is on. Cellular and
/// tunnel interfaces are dropped. iOS keeps the single-seed behaviour (its personal hotspot is a
/// separate story) and returns nothing here.
/// </para>
/// </summary>
public static partial class LocalNetworks
{
    /// <summary>Every private LAN-side network the phone is on right now, with real masks. Empty when
    /// the platform cannot enumerate (iOS, or an OEM where the Java API yields nothing).</summary>
    public static IReadOnlyList<LocalNetwork> Enumerate()
    {
        try
        {
            return EnumeratePlatform()
                .Where(n => !n.IsWan && n.IsPrivate)
                .ToList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Network] Local network enumeration failed: {ex.Message}");
            return Array.Empty<LocalNetwork>();
        }
    }

    /// <summary>
    /// The one extra seed the scanner still takes from the socket trick: the phone's default-route
    /// address, but only when it is a private address that none of the enumerated networks already
    /// covers. On a phone whose Java enumeration yields nothing this is the pre-#920 behaviour
    /// (sweep the /24 around the phone); with the enumeration working it is null, so a cellular
    /// address never seeds a sweep of the carrier's network.
    /// </summary>
    public static string? ScanSeed()
    {
        var ip = NetworkInfo.GetLocalIPv4();
        if (!IPAddress.TryParse(ip, out var address) || !LocalNetwork.IsPrivateAddress(address))
            return null;

        return LocalNetwork.Containing(Enumerate(), address) is null ? ip : null;
    }

    /// <summary>
    /// The hotspot the phone is hosting, when that is the only LAN the phone has (no Wi-Fi of its
    /// own): the TV has to join this hotspot to be found, and this is the address to put in the TV's
    /// Host PC IP field. Null otherwise.
    /// </summary>
    public static LocalNetwork? HotspotOnly()
    {
        var networks = Enumerate();
        var hotspot = networks.FirstOrDefault(n => n.IsHotspot);
        return hotspot is not null && networks.All(n => n.IsHotspot) ? hotspot : null;
    }

    private static partial IEnumerable<LocalNetwork> EnumeratePlatform();
}
