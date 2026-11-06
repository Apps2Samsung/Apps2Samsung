using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// This device's own IP addresses, for the debug log. What a TV sees as the Host PC IP is the
    /// address the OS sends from toward that TV, which <see cref="Toward"/> asks the OS for.
    /// </summary>
    public static class LocalAddress
    {
        /// <summary>
        /// The local address the OS would send from to reach <paramref name="remoteIp"/>: a UDP socket
        /// "connected" to it picks the route without sending a packet. Null when there is no route or
        /// the address does not parse.
        /// </summary>
        public static string? Toward(string? remoteIp, int port = 8001)
        {
            if (!IPAddress.TryParse(remoteIp, out var remote))
                return null;
            try
            {
                using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(remote, port);
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch (SocketException)
            {
                return null;
            }
        }

        /// <summary>
        /// Every IPv4 address on an interface that is up, loopback left out, as "address (interface)".
        /// Empty where the platform will not list interfaces.
        /// </summary>
        public static IReadOnlyList<string> AllIpv4()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => $"{a.Address} ({n.Name})"))
                    .ToList();
            }
            catch (System.Exception ex) when (ex is NetworkInformationException or System.PlatformNotSupportedException or System.InvalidOperationException)
            {
                return new List<string>();
            }
        }
    }
}
