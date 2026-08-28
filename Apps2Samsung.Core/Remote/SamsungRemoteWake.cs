using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// Powers a TV back on with Wake-on-LAN (#544).
    /// <para>
    /// A sleeping Samsung TV answers nothing — no REST API on 8001, no remote channel on 8002 — so
    /// <see cref="SamsungRemoteKeys.Power"/> can only ever turn a set OFF. The one thing a sleeping
    /// set still listens for is a magic packet, which is why the MAC is captured from
    /// <c>/api/v2/</c> while the TV is awake and kept on <see cref="Models.NetworkDevice.MacAddress"/>
    /// (and cached per TV by each head) for use later.
    /// </para>
    /// Requires the TV's own "Power On with Mobile" / network standby setting to be enabled, and a
    /// LAN that passes broadcast traffic — so waking can legitimately fail on a TV that is reachable
    /// when awake. Callers should treat a false return as "it didn't wake", not as an error.
    /// </summary>
    public static class SamsungRemoteWake
    {
        // Both the historical discard port and the WOL-assigned one; TVs differ in which they watch,
        // and sending to both costs one extra datagram.
        private static readonly int[] WakePorts = { 9, 7 };

        /// <summary>
        /// Sends the magic packet for <paramref name="macAddress"/>. Accepts the usual separators
        /// (<c>AA:BB:CC:DD:EE:FF</c>, dashes, or bare hex). Returns false when the MAC can't be
        /// parsed or every send failed.
        /// </summary>
        public static bool Wake(string? macAddress)
        {
            var mac = ParseMac(macAddress);
            if (mac is null)
            {
                Trace.WriteLine($"[wake] '{macAddress}' is not a MAC address — cannot wake.");
                return false;
            }

            // Magic packet: six 0xFF bytes, then the MAC sixteen times over.
            var packet = new byte[102];
            for (int i = 0; i < 6; i++)
                packet[i] = 0xFF;
            for (int repeat = 0; repeat < 16; repeat++)
                Buffer.BlockCopy(mac, 0, packet, 6 + repeat * 6, 6);

            var sent = false;
            foreach (var target in BroadcastTargets())
            {
                foreach (var port in WakePorts)
                {
                    try
                    {
                        using var udp = new UdpClient { EnableBroadcast = true };
                        udp.Send(packet, packet.Length, new IPEndPoint(target, port));
                        sent = true;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[wake] send to {target}:{port} failed: {ex.Message}");
                    }
                }
            }

            Trace.WriteLine(sent
                ? $"[wake] magic packet sent for {macAddress}."
                : $"[wake] could not send a magic packet for {macAddress}.");
            return sent;
        }

        /// <summary>
        /// Wakes the TV and waits for its REST API to come back, which is the point at which the
        /// remote channel will accept a connection. Re-sends the packet on each attempt — a set that
        /// missed the first one (asleep mid-boot, a dropped broadcast) usually catches a later one.
        /// Returns false if it never answered within <paramref name="timeout"/>.
        /// </summary>
        public static async Task<bool> WakeAndWaitAsync(
            string tvIpAddress,
            string? macAddress,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (!Wake(macAddress))
                return false;

            var deadline = DateTime.UtcNow + timeout;
            var attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                var capability = await SamsungRemoteClient.ProbeAsync(tvIpAddress, cancellationToken)
                    .ConfigureAwait(false);
                if (capability.Supported && capability.IsAwake)
                {
                    Trace.WriteLine($"[wake] {tvIpAddress} is awake after {++attempt} check(s).");
                    return true;
                }

                // A TV that wakes on the second or third packet is common enough to be worth retrying.
                if (++attempt % 3 == 0)
                    Wake(macAddress);
            }

            Trace.WriteLine($"[wake] {tvIpAddress} did not come up within {timeout.TotalSeconds:0}s.");
            return false;
        }

        // Directed broadcast per local IPv4 interface first (routers drop 255.255.255.255 more often
        // than a subnet broadcast), plus the global broadcast as a fallback.
        private static IPAddress[] BroadcastTargets()
        {
            var targets = new System.Collections.Generic.List<IPAddress>();

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                            unicast.IPv4Mask is null)
                            continue;

                        var address = unicast.Address.GetAddressBytes();
                        var mask = unicast.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                            broadcast[i] = (byte)(address[i] | (mask[i] ^ 0xFF));

                        targets.Add(new IPAddress(broadcast));
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wake] could not enumerate interfaces: {ex.Message}");
            }

            targets.Add(IPAddress.Broadcast);
            return targets.Distinct().ToArray();
        }

        private static byte[]? ParseMac(string? macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress))
                return null;

            var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray());
            if (hex.Length != 12)
                return null;

            var mac = new byte[6];
            for (int i = 0; i < 6; i++)
                mac[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            return mac;
        }
    }
}
