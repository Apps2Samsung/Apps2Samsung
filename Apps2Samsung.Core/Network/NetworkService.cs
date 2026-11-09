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
        private readonly Func<IEnumerable<LocalNetwork>>? _localNetworksProvider;
        private readonly int? _maxConcurrentHosts;

        // Per-scan probe tally behind LastScanBlockedByOs; reset when a sweep starts.
        private int _probesInScan;
        private int _probesBlockedInScan;

        /// <inheritdoc/>
        public bool LastScanBlockedByOs { get; private set; }

        /// <param name="tvNameResolver">Resolves a found TV's friendly name (SDB-backed). Optional.</param>
        /// <param name="macVendorLookup">Enriches a found device with its manufacturer. Optional; desktop-only in practice.</param>
        /// <param name="customIpProvider">Supplies a user-configured extra IP to fold into the scan. Optional.</param>
        /// <param name="maxConcurrentHosts">How many hosts the sweep probes at once (two sockets each).
        /// Null probes the whole range in one go, as the desktop always has; a phone sets a cap so a
        /// /24 sweep stays under the process's open-file limit (#824).</param>
        /// <param name="localNetworksProvider">The LAN-side networks this device is on, from a head
        /// that can enumerate them better than .NET does here (Android: the Java interface API, which
        /// is the only one that sees the phone's own hotspot). Swept in addition to whatever the .NET
        /// enumeration finds; read fresh on every scan. Optional.</param>
        public NetworkService(
            ITvNameResolver? tvNameResolver = null,
            IMacVendorLookup? macVendorLookup = null,
            Func<string?>? customIpProvider = null,
            int? maxConcurrentHosts = null,
            Func<IEnumerable<LocalNetwork>>? localNetworksProvider = null)
        {
            _tvNameResolver = tvNameResolver;
            _macVendorLookup = macVendorLookup;
            _customIpProvider = customIpProvider;
            _maxConcurrentHosts = maxConcurrentHosts;
            _localNetworksProvider = localNetworksProvider;
        }

        private string? CustomIp => _customIpProvider?.Invoke();

        // The head-supplied networks, held to the same bar as a .NET interface: IPv4, not loopback,
        // not a range a TV never lives on, and a real mask. A provider that throws costs nothing.
        private List<LocalNetwork> ProvidedNetworks()
        {
            if (_localNetworksProvider is null)
                return new List<LocalNetwork>();

            try
            {
                return _localNetworksProvider()
                    .Where(n => n.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(n => !IPAddress.IsLoopback(n.Address) && !n.Address.Equals(IPAddress.Any))
                    .Where(n => IsScannableAddress(n.Address))
                    .Where(n => !n.Mask.Equals(IPAddress.Any))
                    .ToList();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Network] Local network provider failed: {ex.Message}");
                return new List<LocalNetwork>();
            }
        }

        /// <inheritdoc/>
        public string? GetLocalIpOnSubnetOf(string? remoteIp)
        {
            if (!IPAddress.TryParse(remoteIp, out var remote))
                return null;

            var networks = GetLocalNetworkInfos(includeCustomIp: false)
                .Select(info => new LocalNetwork(info.Address, info.Mask));
            return LocalNetwork.Containing(networks, remote)?.Address.ToString();
        }

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
                // Same bar as the network scan (see FindTizenTvsAsync), so the manual and the
                // discovered paths always agree on a TV (#523): a TV on the SDB debug port (26101)
                // is ready to install to; a TV that answers only on the 8001 REST API is listed as
                // "not ready" — it's a real Samsung TV, just without (active) Developer Mode. The
                // manual path used to accept 26101 only, so a Developer-Mode-off TV that the scan
                // missed could not be rescued by typing its IP either, and the Remote / TV toolbox
                // — which don't need Developer Mode at all — were unreachable (#663).
                var ports = await ProbeTvPortsAsync(ip, cancellationToken);
                if (!ports.DebugPortOpen && !ports.ApiPortOpen)
                    return null;

                var device = new NetworkDevice
                {
                    IpAddress = ip,
                    DebugPortOpen = ports.DebugPortOpen
                };

                if (ports.DebugPortOpen)
                {
                    device.Manufacturer = await GetManufacturerFromIp(ip);
                    if (device.Manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) == true)
                        device.DeviceName = await GetTvNameAsync(ip);
                }

                return device;
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
            Interlocked.Exchange(ref _probesInScan, 0);
            Interlocked.Exchange(ref _probesBlockedInScan, 0);
            LastScanBlockedByOs = false;

            // Deduplicate by actual network address so overlapping interfaces don't double-scan
            var uniqueNetworks = localInfos
                .Select(info => (
                    Network: GetNetworkAddress(info.Address, info.Mask),
                    Broadcast: GetBroadcastAddress(info.Address, info.Mask)
                ))
                .DistinctBy(r => r.Network.ToString())
                .ToList();

            using var gate = _maxConcurrentHosts is { } limit ? new SemaphoreSlim(limit, limit) : null;
            await Task.WhenAll(uniqueNetworks.SelectMany(range =>
                GetHostAddresses(range.Network, range.Broadcast)
                    .Select(async ip =>
                    {
                        var acquired = false;
                        try
                        {
                            if (gate is not null)
                            {
                                await gate.WaitAsync(cancellationToken);
                                acquired = true;
                            }

                            var ports = await ProbeTvPortsAsync(ip, cancellationToken);
                            if (ports.DebugPortOpen)
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
                            // there but not ready (Developer Mode off or not fully active). Surface
                            // it as "not ready" so the user gets an actionable hint instead of "no
                            // devices" — and so the Remote / TV toolbox, which don't need Developer
                            // Mode, can reach it. TizenDeveloperInfo enriches it via /api/v2/
                            // (name, developerMode, developerIP).
                            else if (ports.ApiPortOpen)
                            {
                                Trace.WriteLine($"[Scan] {ip}: debug port {TizenDevPort} closed, REST API {SamsungTvApiPort} open — listed as not ready.");
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
                        finally
                        {
                            if (acquired)
                                gate!.Release();
                        }
                    })));

            Trace.WriteLine($"Scan complete! Found {foundDevices.Count} device(s) (debug port {TizenDevPort} or REST API {SamsungTvApiPort}).");

            // Nothing found and not one probe got as far as the network: the OS refused them all.
            // On a LAN without a TV the probes time out or are refused instead, so this does not
            // fire for an ordinary empty result (#824).
            var probes = Volatile.Read(ref _probesInScan);
            var blockedProbes = Volatile.Read(ref _probesBlockedInScan);
            LastScanBlockedByOs = foundDevices.Count == 0 && probes > 0 && blockedProbes == probes;
            if (LastScanBlockedByOs)
                Trace.WriteLine($"[Scan] all {probes} probes failed before reaching the network (unreachable / access denied): the OS is blocking this app's local network access.");

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
                .Concat(ProvidedNetworks().Select(n => n.Address.ToString()))
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
        /// <summary>
        /// Probes the two ports that identify a Samsung TV — the SDB debug port (26101) and the REST
        /// API (8001) — for one host. The probes run concurrently and each gets its OWN
        /// <see cref="NetworkScanTimeoutMs"/> budget.
        ///
        /// They used to run one after the other under a single shared 1 s budget, which silently
        /// dropped every TV with Developer Mode off: some sets (e.g. a 2021 Tizen 6 AU7000) take
        /// ~1.03 s to send the RST for the closed 26101, so the shared token was already cancelled
        /// by the time the 8001 probe started, and that probe "failed" via
        /// OperationCanceledException although the port answers in ~30 ms. The TV then never showed
        /// up at all — not even as "not ready" — and the manual-IP path rejected it too (#663).
        /// Running both probes side by side keeps the per-host scan time at one budget instead of two.
        /// </summary>
        private async Task<(bool DebugPortOpen, bool ApiPortOpen)> ProbeTvPortsAsync(string ip, CancellationToken cancellationToken)
        {
            var debugProbe = ProbeWithTimeoutAsync(ip, TizenDevPort, cancellationToken);
            var apiProbe = ProbeWithTimeoutAsync(ip, SamsungTvApiPort, cancellationToken);
            await Task.WhenAll(debugProbe, apiProbe);
            return (debugProbe.Result, apiProbe.Result);
        }

        // One port probe under its own per-probe timeout, linked to the caller's token so a
        // cancelled scan still stops every in-flight connect immediately.
        private async Task<bool> ProbeWithTimeoutAsync(string ip, int port, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(NetworkScanTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            return await IsPortOpenAsync(ip, port, linked.Token);
        }

        public async Task<bool> IsPortOpenAsync(string ip, int port, CancellationToken ct)
        {
            Interlocked.Increment(ref _probesInScan);
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

                // Failed before a packet went out. iOS answers every LAN connect with "No route to
                // host" while its Local Network permission is denied or still being asked; macOS
                // says "Operation not permitted". Tallied for LastScanBlockedByOs (#824).
                if (ex is SocketException blocked && blocked.SocketErrorCode is
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.AccessDenied)
                    Interlocked.Increment(ref _probesBlockedInScan);

                if (!normal)
                {
                    var code = (ex as SocketException)?.SocketErrorCode.ToString() ?? ex.GetType().Name;
                    Trace.WriteLine($"[Scan] connect {ip}:{port} failed unexpectedly ({code}): {ex.Message}");
                }
                return false;
            }
        }

        // Returns all local interface IPs with their actual subnet masks: the .NET enumeration plus
        // whatever the head's own provider adds (the same address from both sources counts once).
        // Falls back to /24 for the user-supplied custom IP since its mask can't be discovered;
        // includeCustomIp=false leaves that one out for callers that want this device's own
        // networks only (the custom IP is a TV address on the desktop, not one of ours).
        private List<(IPAddress Address, IPAddress Mask)> GetLocalNetworkInfos(bool virtualScan = false, bool includeCustomIp = true)
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

            foreach (var provided in ProvidedNetworks())
            {
                if (!infos.Any(i => i.Address.Equals(provided.Address)))
                    infos.Add((provided.Address, provided.Mask));
            }

            var customIpStr = includeCustomIp ? CustomIp : null;
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

        // Looks up the subnet mask assigned to a local interface IP. Returns null when it can't be
        // determined — including on Android, where IPv4Mask isn't implemented and throws; callers
        // fall back to /24 rather than losing the comparison entirely.
        private static IPAddress? GetMaskForLocalIp(IPAddress target)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    .FirstOrDefault(ua => ua.Address.Equals(target))
                    ?.IPv4Mask;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Network] Subnet mask lookup failed for {target}: {ex.Message}");
                return null;
            }
        }

        /// <inheritdoc/>
        public string? GetLocalIPAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var address = (socket.LocalEndPoint as IPEndPoint)?.Address;
                // With no route (Wi-Fi still re-associating after a wake) the OS answers with
                // 0.0.0.0 or loopback; neither is "this machine" to a TV, so say "unknown" rather
                // than let 127.0.0.1 reach a verdict or the screen (#922).
                if (address is null || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
                    return null;
                return address.ToString();
            }
            catch (SocketException)
            {
                return null;
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

            // Use the actual mask from the local interface (the head's own enumeration first: on
            // Android it is the only one that has masks at all); fall back to /24 if not found.
            var mask = ProvidedNetworks().FirstOrDefault(n => n.Address.Equals(a))?.Mask
                ?? GetMaskForLocalIp(a)
                ?? LocalNetwork.DefaultMask;

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
