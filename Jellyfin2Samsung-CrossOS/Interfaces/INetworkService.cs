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
        Task<string?> GetManufacturerFromIp(string ipAddress);
        bool IsDifferentSubnet(string ip1, string ip2);
        Task<IReadOnlyList<NetworkInterfaceOption>> GetNetworkInterfaceOptionsAsync();
    }
}
