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
        string GetLocalIPAddress();
        string InvertIPAddress(string ipAddress);
        bool IsDifferentSubnet(string ip1, string ip2);
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
