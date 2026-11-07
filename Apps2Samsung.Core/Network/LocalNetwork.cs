using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One IPv4 network this device sits on: an address of its own plus the subnet mask, and the
    /// interface it came from when known. The scanner sweeps one of these per LAN-side interface.
    /// <para>
    /// Exists because the .NET interface enumeration is not usable everywhere: on Android the
    /// adapter type is never Ethernet/Wireless80211 and <c>IPv4Mask</c> throws, so a head that can
    /// read interfaces through its own platform API hands the scanner a list of these instead
    /// (see <c>NetworkService</c>'s <c>localNetworksProvider</c>). A phone hosting a Wi-Fi hotspot
    /// has two such networks, the hotspot and whatever the phone itself is on; a TV on the hotspot
    /// is only found when both are swept (#920).
    /// </para>
    /// </summary>
    /// <param name="Address">This device's own address on the network.</param>
    /// <param name="Mask">The subnet mask, e.g. 255.255.255.0.</param>
    /// <param name="InterfaceName">The OS interface name (<c>wlan0</c>, <c>swlan0</c>, ...), if known.</param>
    public sealed record LocalNetwork(IPAddress Address, IPAddress Mask, string? InterfaceName = null)
    {
        // Android names its hotspot (soft AP) interface after the chipset vendor's driver; these
        // are the ones seen on stock Android, Samsung One UI and the common OEM skins.
        private static readonly string[] HotspotInterfacePrefixes = { "ap", "swlan", "softap", "wlan1" };

        // Cellular / tunnel interfaces: a TV is never behind these, and their addresses are the
        // carrier's (CGNAT or a private range the carrier owns), not a LAN this device is on.
        private static readonly string[] WanInterfacePrefixes = { "rmnet", "ccmni", "clat", "tun", "ppp", "v4-", "pdp" };

        /// <summary>The network address (<see cref="Address"/> masked).</summary>
        public IPAddress NetworkAddress => Masked(Address, Mask);

        /// <summary>True when <paramref name="other"/> is on this network.</summary>
        public bool Contains(IPAddress other) =>
            other.AddressFamily == AddressFamily.InterNetwork && Masked(other, Mask).Equals(NetworkAddress);

        /// <summary>True when the interface name says this is the device's own Wi-Fi hotspot.</summary>
        public bool IsHotspot => NameStartsWithAny(InterfaceName, HotspotInterfacePrefixes);

        /// <summary>True when the interface name says this is cellular or a tunnel, never a LAN.</summary>
        public bool IsWan => NameStartsWithAny(InterfaceName, WanInterfacePrefixes);

        /// <summary>Whether <see cref="Address"/> is an RFC 1918 private address, the only kind a home LAN or hotspot hands out.</summary>
        public bool IsPrivate => IsPrivateAddress(Address);

        /// <summary>A /24, the mask assumed whenever the real one cannot be read.</summary>
        public static IPAddress DefaultMask { get; } = IPAddress.Parse("255.255.255.0");

        /// <summary>The mask for a CIDR prefix length (24 → 255.255.255.0). Out-of-range lengths give <see cref="DefaultMask"/>.</summary>
        public static IPAddress MaskFromPrefixLength(int prefixLength)
        {
            if (prefixLength < 1 || prefixLength > 32)
                return DefaultMask;

            var bits = prefixLength == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefixLength);
            return new IPAddress(new[]
            {
                (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits,
            });
        }

        /// <summary>True for 10/8, 172.16/12 and 192.168/16.</summary>
        public static bool IsPrivateAddress(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return false;

            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>
        /// Of <paramref name="networks"/>, the one that contains <paramref name="remote"/>: the
        /// network to talk to that host on, and the local address the host sees us as. Null when
        /// none does (the host is behind a router, or the list is empty).
        /// </summary>
        public static LocalNetwork? Containing(IEnumerable<LocalNetwork> networks, IPAddress remote) =>
            networks.FirstOrDefault(n => n.Contains(remote));

        /// <summary>
        /// String form of <see cref="Containing"/> for the heads: the local IP to show next to a TV,
        /// or null when the TV is not on any of these networks.
        /// </summary>
        public static string? LocalIpFor(IEnumerable<LocalNetwork> networks, string? remoteIp) =>
            IPAddress.TryParse(remoteIp, out var remote) ? Containing(networks, remote)?.Address.ToString() : null;

        private static IPAddress Masked(IPAddress address, IPAddress mask)
        {
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var result = new byte[4];
            for (var i = 0; i < 4; i++)
                result[i] = (byte)(a[i] & m[i]);
            return new IPAddress(result);
        }

        private static bool NameStartsWithAny(string? name, string[] prefixes) =>
            !string.IsNullOrEmpty(name)
            && prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
