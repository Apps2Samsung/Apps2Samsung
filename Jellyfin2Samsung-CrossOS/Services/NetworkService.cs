using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class NetworkService : INetworkService
    {
        private readonly ITizenInstallerService _tizenInstaller;
        private static readonly HttpClient _httpClient = new HttpClient();

        public NetworkService(ITizenInstallerService tizenInstaller)
        {
            _tizenInstaller = tizenInstaller;
        }

        public async Task<IEnumerable<NetworkDevice>> GetLocalTizenAddresses(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            return await FindTizenTvsAsync(cancellationToken, virtualScan);
        }

        public async Task<NetworkDevice?> ValidateManualTizenAddress(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = new CancellationTokenSource(Constants.Defaults.NetworkScanTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, cancellationToken);

                if (await IsPortOpenAsync(ip, Constants.Ports.TizenDevPort, linkedCts.Token))
                {
                    if (await IsPortOpenAsync(ip, Constants.Ports.SamsungTvApiPort, linkedCts.Token))
                    {
                        var manufacturer = await GetManufacturerFromIp(ip);
                        var device = new NetworkDevice
                        {
                            IpAddress = ip,
                            Manufacturer = manufacturer
                        };

                        if (manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) == true)
                            device.DeviceName = await _tizenInstaller.GetTvNameAsync(ip);

                        return device;
                    }
                    return null;
                }

                return null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ValidateManualTizenAddress] Error validating IP '{ip}': {ex}");
                return null;
            }
        }

        public async Task<IEnumerable<NetworkDevice>> FindTizenTvsAsync(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            var foundDevices = new List<NetworkDevice>();
            var localInfos = GetLocalNetworkInfos(virtualScan);
            var lockObject = new object();

            // Deduplicate by actual network address so overlapping interfaces don't double-scan
            var uniqueNetworks = localInfos
                .Select(info => (
                    Network: GetNetworkAddress(info.Address, info.Mask),
                    Broadcast: GetBroadcastAddress(info.Address, info.Mask)
                ))
                .DistinctBy(r => r.Network.ToString())
                .ToList();

            await Task.WhenAll(uniqueNetworks.SelectMany(range =>
                GetHostAddresses(range.Network, range.Broadcast)
                    .Select(async ip =>
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(Constants.Defaults.NetworkScanTimeoutMs);
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                                cts.Token, cancellationToken);
                            if (await IsPortOpenAsync(ip, Constants.Ports.TizenDevPort, linkedCts.Token))
                            {
                                var manufacturer = await GetManufacturerFromIp(ip);
                                var device = new NetworkDevice
                                {
                                    IpAddress = ip,
                                    Manufacturer = manufacturer
                                };
                                lock (lockObject)
                                {
                                    foundDevices.Add(device);
                                }
                                if (manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    device.DeviceName = await _tizenInstaller.GetTvNameAsync(ip);
                                }
                            }
                        }
                        catch { /* Ignore scan failures */ }
                    })));

            Trace.WriteLine($"Scan complete! Found {foundDevices.Count} devices with port {Constants.Ports.TizenDevPort} open.");
            return foundDevices;
        }
        public IEnumerable<IPAddress> GetRelevantLocalIPs(bool virtualScan = false)
        {
            var baseIps = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni =>
                    virtualScan
                        ? true
                        : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                           ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip.Address))
                .Select(ip => ip.Address.ToString())
                .ToList();

            var additionalIps = Enumerable.Empty<string>();
            if (!string.IsNullOrEmpty(AppSettings.Default.UserCustomIP))
            {
                try
                {
                    // Validate it's a valid IP by parsing, then use the string
                    IPAddress.Parse(AppSettings.Default.UserCustomIP);
                    additionalIps = new[] { AppSettings.Default.UserCustomIP };
                }
                catch (FormatException)
                {
                    additionalIps = Enumerable.Empty<string>();
                }
            }

            return baseIps.Concat(additionalIps)
                .Distinct()
                .Select(IPAddress.Parse); // Convert back to IPAddress
        }
        public async Task<bool> IsPortOpenAsync(string ip, int port, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, port, ct);
                var timeoutTask = Task.Delay(Timeout.Infinite, ct);

                var completedTask = await Task.WhenAny(connectTask.AsTask(), timeoutTask);
                if (completedTask == connectTask.AsTask())
                {
                    await connectTask; // Ensure connection succeeded
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // Returns all local interface IPs with their actual subnet masks.
        // Falls back to /24 for the user-supplied custom IP since its mask can't be discovered.
        private List<(IPAddress Address, IPAddress Mask)> GetLocalNetworkInfos(bool virtualScan = false)
        {
            var infos = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni =>
                    virtualScan ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(ua => !IPAddress.IsLoopback(ua.Address))
                .Where(ua => ua.IPv4Mask != null && !ua.IPv4Mask.Equals(IPAddress.Any))
                .Select(ua => (Address: ua.Address, Mask: ua.IPv4Mask))
                .ToList();

            if (!string.IsNullOrEmpty(AppSettings.Default.UserCustomIP) &&
                IPAddress.TryParse(AppSettings.Default.UserCustomIP, out var customIp))
            {
                // Reuse the mask from a local interface whose network contains the custom IP;
                // otherwise fall back to /24 so we still scan the right /24 segment.
                var fallback = IPAddress.Parse("255.255.255.0");
                var matchingMask = infos
                    .FirstOrDefault(i =>
                        GetNetworkAddress(i.Address, i.Mask).Equals(GetNetworkAddress(customIp, i.Mask)))
                    .Mask ?? fallback;
                infos.Add((customIp, matchingMask));
            }

            return infos;
        }

        private static IPAddress GetNetworkAddress(IPAddress ip, IPAddress mask)
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ipBytes[i] & maskBytes[i]);
            return new IPAddress(result);
        }

        private static IPAddress GetBroadcastAddress(IPAddress ip, IPAddress mask)
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
            return new IPAddress(result);
        }

        // Enumerates usable host addresses for a subnet (excludes network and broadcast addresses).
        // Caps at 1022 hosts (/22) to keep scans practical; larger subnets are narrowed to the
        // /24 block that contains the network address.
        private static IEnumerable<string> GetHostAddresses(IPAddress networkAddress, IPAddress broadcastAddress)
        {
            uint netInt = IpToUInt(networkAddress);
            uint broadInt = IpToUInt(broadcastAddress);
            uint hostCount = broadInt - netInt - 1;

            if (hostCount > 1022)
            {
                // Narrow to /24 to avoid scanning thousands of addresses
                var bytes = networkAddress.GetAddressBytes();
                netInt = IpToUInt(new IPAddress(new byte[] { bytes[0], bytes[1], bytes[2], 0 }));
                broadInt = netInt + 255;
            }

            for (uint i = netInt + 1; i < broadInt; i++)
                yield return UIntToIp(i);
        }

        private static uint IpToUInt(IPAddress ip)
        {
            var bytes = ip.GetAddressBytes();
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        private static string UIntToIp(uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{bytes[3]}";
        }

        // Looks up the subnet mask assigned to a local interface IP.
        private static IPAddress? GetMaskForLocalIp(IPAddress target)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                .FirstOrDefault(ua => ua.Address.Equals(target))
                ?.IPv4Mask;
        }

        public async Task<string?> GetManufacturerFromIp(string ipAddress)
        {
            string? macAddress = await GetMacAddressFromIp(ipAddress);
            return string.IsNullOrEmpty(macAddress)
                ? null
                : await GetManufacturerFromMac(macAddress);
        }

        private static async Task<string?> GetMacAddressFromIp(string ipAddress)
        {
            string arpArgs = PlatformService.GetArpArguments(ipAddress);

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "arp",
                        Arguments = arpArgs,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var match = RegexPatterns.Network.MacAddress.Match(output);
                return match.Success ? match.Value : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> GetManufacturerFromMac(string macAddress)
        {
            try
            {
                string oui = macAddress
                    .Replace(":", "")
                    .Replace("-", "")
                    .Substring(0, 6)
                    .ToUpper();

                return await _httpClient.GetStringAsync($"https://api.macvendors.com/{oui}");
            }
            catch
            {
                return null;
            }
        }
        public string GetLocalIPAddress()
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                var endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint?.Address.ToString();
            }
        }
        public string InvertIPAddress(string ipAddress)
        {
            var parts = ipAddress.Split('.');
            if (parts.Length != 4) throw new FormatException("Invalid IPv4 address.");
            Array.Reverse(parts);
            return string.Join(".", parts);
        }
        public bool IsDifferentSubnet(string ip1, string ip2)
        {
            if (!IPAddress.TryParse(ip1, out var a) || !IPAddress.TryParse(ip2, out var b))
                return false;

            // Use the actual mask from the local interface; fall back to /24 if not found
            var mask = GetMaskForLocalIp(a) ?? IPAddress.Parse("255.255.255.0");

            var aBytes = a.GetAddressBytes();
            var bBytes = b.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            for (int i = 0; i < 4; i++)
            {
                if ((aBytes[i] & maskBytes[i]) != (bBytes[i] & maskBytes[i]))
                    return true;
            }
            return false;
        }
        public Task<IReadOnlyList<NetworkInterfaceOption>> GetNetworkInterfaceOptionsAsync()
        {
            return Task.Run(() =>
            {
                var result = new List<NetworkInterfaceOption>();

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        if (IPAddress.IsLoopback(ua.Address))
                            continue;

                        result.Add(new NetworkInterfaceOption
                        {
                            Name = ni.Name,
                            IpAddress = ua.Address.ToString()
                        });
                    }
                }

                return (IReadOnlyList<NetworkInterfaceOption>)result;
            });
        }
    }
}
