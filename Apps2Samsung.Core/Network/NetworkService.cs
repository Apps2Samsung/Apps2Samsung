using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class NetworkService : INetworkService
    {
        // Tizen developer-mode debug port (SDB) and the Samsung TV REST API port.
        private const int TizenDevPort = 26101;
        private const int SamsungTvApiPort = 8001;
        // Per-host connect timeout while scanning a subnet.
        private const int NetworkScanTimeoutMs = 1000;

        private readonly ITvNameResolver? _tvNameResolver;
        private readonly IMacVendorLookup? _macVendorLookup;
        private readonly Func<string?>? _customIpProvider;

        /// <param name="tvNameResolver">Resolves a found TV's friendly name (SDB-backed). Optional.</param>
        /// <param name="macVendorLookup">Enriches a found device with its manufacturer. Optional; desktop-only in practice.</param>
        /// <param name="customIpProvider">Supplies a user-configured extra IP to fold into the scan. Optional.</param>
        public NetworkService(
            ITvNameResolver? tvNameResolver = null,
            IMacVendorLookup? macVendorLookup = null,
            Func<string?>? customIpProvider = null)
        {
            _tvNameResolver = tvNameResolver;
            _macVendorLookup = macVendorLookup;
            _customIpProvider = customIpProvider;
        }

        private string? CustomIp => _customIpProvider?.Invoke();

        private Task<string?> GetManufacturerFromIp(string ip) =>
            _macVendorLookup is null ? Task.FromResult<string?>(null) : _macVendorLookup.GetManufacturerFromIpAsync(ip);

        private Task<string> GetTvNameAsync(string ip) =>
            _tvNameResolver is null ? Task.FromResult(string.Empty) : _tvNameResolver.GetTvNameAsync(ip);

        public async Task<IEnumerable<NetworkDevice>> GetLocalTizenAddresses(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            return await FindTizenTvsAsync(cancellationToken, virtualScan);
        }

        public async Task<NetworkDevice?> ValidateManualTizenAddress(string ip, CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = new CancellationTokenSource(NetworkScanTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, cancellationToken);

                // Accept a TV on the SDB debug port (26101) alone — the same bar the network scan
                // uses (see FindTizenTvsAsync). Manual entry previously ALSO required the 8001 REST
                // API to be open, so a TV that's reachable and developer-ready on 26101 but whose
                // 8001 is closed/firewalled/slow (the two probes shared one 1s budget) was wrongly
                // rejected as "Invalid device IP" — even though the exact same TV would have been
                // accepted by the scan. Match the scan so the manual and discovered paths agree (#523).
                if (await IsPortOpenAsync(ip, TizenDevPort, linkedCts.Token))
                {
                    var manufacturer = await GetManufacturerFromIp(ip);
                    var device = new NetworkDevice
                    {
                        IpAddress = ip,
                        Manufacturer = manufacturer
                    };

                    if (manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) == true)
                        device.DeviceName = await GetTvNameAsync(ip);

                    return device;
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
                            using var cts = new CancellationTokenSource(NetworkScanTimeoutMs);
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                                cts.Token, cancellationToken);
                            if (await IsPortOpenAsync(ip, TizenDevPort, linkedCts.Token))
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
                                    device.DeviceName = await GetTvNameAsync(ip);
                                }
                            }
                            // Debug port closed, but the TV REST API (8001) answers: the TV is
                            // there but not ready (Developer Mode not fully active). Surface it as
                            // "not ready" so the user gets an actionable hint instead of "no devices".
                            // DeviceHelper enriches it via /api/v2/ (name, developerMode, developerIP).
                            else if (await IsPortOpenAsync(ip, SamsungTvApiPort, linkedCts.Token))
                            {
                                var device = new NetworkDevice
                                {
                                    IpAddress = ip,
                                    DebugPortOpen = false
                                };
                                lock (lockObject)
                                {
                                    foundDevices.Add(device);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Don't let one host's failure abort the sweep, but no longer swallow it
                            // silently — log so a systematic failure (e.g. an OS-level block) is visible.
                            Trace.WriteLine($"[Scan] host probe failed: {ex.GetType().Name} — {ex.Message}");
                        }
                    })));

            Trace.WriteLine($"Scan complete! Found {foundDevices.Count} device(s) (debug port {TizenDevPort} or REST API {SamsungTvApiPort}).");
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
            var customIp = CustomIp;
            if (!string.IsNullOrEmpty(customIp))
            {
                try
                {
                    // Validate it's a valid IP by parsing, then use the string
                    IPAddress.Parse(customIp);
                    additionalIps = new[] { customIp };
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
                // ConnectAsync honours the caller's cancellation token (a linked timeout CTS), so a
                // closed/filtered port cancels to false. It returns a ValueTask — await it EXACTLY
                // once. The previous code called .AsTask() on the ValueTask twice (plus a trailing
                // await), which on Android/MonoVM faults ("a ValueTask may be consumed only once")
                // and made every probe return false — so no TV was ever detected despite the
                // connects actually happening. Desktop's runtime tolerated the double-consumption.
                await client.ConnectAsync(ip, port, ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false; // normal: per-probe timeout / linked-CTS cancellation
            }
            catch (Exception ex)
            {
                // A subnet sweep expects most probes to fail with refused/timeout/unreachable, so keep
                // those quiet. Anything else is surfaced instead of silently swallowed — notably macOS
                // Local Network privacy, which blocks the connect with "Operation not permitted"
                // (SocketError.AccessDenied). That log line is what tells us a connect was denied by
                // the OS rather than the host simply being absent (see #498).
                var normal = ex is SocketException se && se.SocketErrorCode is
                    SocketError.ConnectionRefused or SocketError.TimedOut or
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable;
                if (!normal)
                {
                    var code = (ex as SocketException)?.SocketErrorCode.ToString() ?? ex.GetType().Name;
                    Trace.WriteLine($"[Scan] connect {ip}:{port} failed unexpectedly ({code}): {ex.Message}");
                }
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
                .Where(ua => IsScannableAddress(ua.Address))
                .Where(ua => ua.IPv4Mask != null && !ua.IPv4Mask.Equals(IPAddress.Any))
                .Select(ua => (Address: ua.Address, Mask: ua.IPv4Mask))
                .ToList();

            var customIpStr = CustomIp;
            if (!string.IsNullOrEmpty(customIpStr) &&
                IPAddress.TryParse(customIpStr, out var customIp))
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

        // Ranges that can appear as a local interface but are never a real home LAN the TV
        // lives on. Scanning them (typically a VPN / tunnel / "secure DNS" adapter sitting on
        // 198.18.0.0/15 or CGNAT) wastes minutes probing phantom hosts and can divert the
        // install route onto the tunnel, causing "connection reset by peer" mid-push.
        // NOTE: the user-supplied custom IP bypasses this (added after the filter), so a
        // deliberate TV address in an unusual range is still scanned.
        private static readonly (uint Network, uint Mask)[] NonScannableRanges = BuildNonScannableRanges();

        private static (uint, uint)[] BuildNonScannableRanges()
        {
            (uint, uint) R(string net, string mask) =>
                (IpToUInt(IPAddress.Parse(net)), IpToUInt(IPAddress.Parse(mask)));

            return new[]
            {
                R("169.254.0.0",  "255.255.0.0"),   // link-local / APIPA
                R("100.64.0.0",   "255.192.0.0"),   // CGNAT (Tailscale, Cloudflare WARP, some ISPs)
                R("198.18.0.0",   "255.254.0.0"),   // RFC 2544 benchmarking (VPN/proxy adapters)
                R("192.0.2.0",    "255.255.255.0"), // TEST-NET-1
                R("198.51.100.0", "255.255.255.0"), // TEST-NET-2
                R("203.0.113.0",  "255.255.255.0"), // TEST-NET-3
            };
        }

        private static bool IsScannableAddress(IPAddress ip)
        {
            uint addr = IpToUInt(ip);
            foreach (var (network, mask) in NonScannableRanges)
            {
                if ((addr & mask) == (network & mask))
                    return false;
            }
            return true;
        }

        // IPv4 ranges only an overlay/VPN hands out, used to spot an active tunnel adapter.
        private static readonly (uint Network, uint Mask)[] VpnRanges =
        {
            (IpToUInt(IPAddress.Parse("100.64.0.0")), IpToUInt(IPAddress.Parse("255.192.0.0"))),   // CGNAT (Tailscale, WARP)
            (IpToUInt(IPAddress.Parse("198.18.0.0")), IpToUInt(IPAddress.Parse("255.254.0.0"))),   // RFC 2544 (VPN/proxy)
            (IpToUInt(IPAddress.Parse("25.0.0.0")),   IpToUInt(IPAddress.Parse("255.0.0.0"))),     // Hamachi
        };

        // Adapter name/description fragments that identify a known VPN / tunnel product.
        private static readonly string[] VpnNameTokens =
        {
            "vpn", "wireguard", "wintun", "tap-windows", "openvpn", "tailscale", "zerotier",
            "hamachi", "nordlynx", "mullvad", "proton", "expressvpn", "surfshark", "cloudflare warp",
        };

        private static bool IsVpnRange(IPAddress ip)
        {
            uint addr = IpToUInt(ip);
            foreach (var (network, mask) in VpnRanges)
            {
                if ((addr & mask) == (network & mask))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Best-effort detection of an active VPN / tunnel adapter, so the UI can warn the user
        /// before they blame the app for a TV that "won't be found". Returns the adapter's
        /// description (or name) when one looks like a VPN, otherwise null. Heuristic only.
        /// </summary>
        public string? GetActiveVpnAdapterName()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    static string Describe(NetworkInterface n) =>
                        string.IsNullOrWhiteSpace(n.Description) ? n.Name : n.Description;

                    // 1) Adapter type that is inherently a tunnel / dial-up VPN.
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                        return Describe(ni);

                    // 2) Name/description matches a known VPN / tunnel product.
                    var text = $"{ni.Name} {ni.Description}";
                    if (VpnNameTokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        return Describe(ni);

                    // 3) An IPv4 in a range only a VPN/overlay hands out.
                    if (ni.GetIPProperties().UnicastAddresses
                        .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Any(ua => IsVpnRange(ua.Address)))
                        return Describe(ni);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VPN] detection failed: {ex.Message}");
            }

            return null;
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
