using Apps2Samsung.Models;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface INetworkService
    {
        Task<IEnumerable<NetworkDevice>> GetLocalTizenAddresses(CancellationToken cancellationToken = default, bool virtualScan = false);
        Task<NetworkDevice?> ValidateManualTizenAddress(string ip, CancellationToken cancellationToken = default);
        IEnumerable<IPAddress> GetRelevantLocalIPs(bool virtualScan = false);
        Task<bool> IsPortOpenAsync(string ip, int port, CancellationToken ct);
        /// <summary>
        /// The address of the interface that carries this machine's default route, read now (a UDP
        /// socket "connected" towards the internet; nothing is sent). Null when there is no route or
        /// the OS answers with loopback / 0.0.0.0, never a loopback address.
        /// </summary>
        string? GetLocalIPAddress();
        string InvertIPAddress(string ipAddress);
        bool IsDifferentSubnet(string ip1, string ip2);

        /// <summary>
        /// This device's own address on the network that <paramref name="remoteIp"/> is on: the
        /// address a TV at that IP reaches us on, and so the one to show as "this device" next to
        /// it and to put in the TV's Host PC IP field. Null when the TV is not on any local network
        /// (behind a router) or the IP does not parse. Read fresh from the interfaces on every call.
        /// </summary>
        string? GetLocalIpOnSubnetOf(string? remoteIp);
        Task<IReadOnlyList<NetworkInterfaceOption>> GetNetworkInterfaceOptionsAsync();
        string? GetActiveVpnAdapterName();

        /// <summary>
        /// True when the last subnet scan found nothing and every probe failed before a packet
        /// reached the network (host/network unreachable, access denied). That is the signature of
        /// the OS blocking this app's local network access (the iOS Local Network permission, macOS
        /// TCC) rather than of a LAN without a TV, where probes time out or are refused (#824).
        /// </summary>
        bool LastScanBlockedByOs { get; }
    }
}
