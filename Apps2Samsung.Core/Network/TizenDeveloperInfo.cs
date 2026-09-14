using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Reads a TV's Developer-Mode state from the Samsung REST API (port 8001, <c>/api/v2/</c>) and
    /// enriches scan results with it, so a scanned <see cref="NetworkDevice"/> carries
    /// <c>developerMode</c> / <c>developerIP</c> as well as the friendly name and model.
    ///
    /// Both heads need this: the pre-install guards (<see cref="InstallGuards"/>) and the not-ready
    /// hints (<see cref="TizenDeviceReadiness"/>) can only tell the user what to fix when those fields
    /// are filled in. It used to live in the desktop head only (TizenApiClient + DeviceHelper), which
    /// is why the mobile head could not warn about Developer Mode being off or pointing at another
    /// machine — the install just failed later on the DUID read.
    /// </summary>
    public static class TizenDeveloperInfo
    {
        // These requests only ever go to a TV on the LAN, so they need no auth handler; a short
        // timeout keeps one silent host from stalling a whole scan.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>
        /// Scans the network for TVs and enriches each with its Developer-Mode info — the device list
        /// both heads show in their TV picker.
        /// </summary>
        public static async Task<List<NetworkDevice>> ScanAsync(
            INetworkService network, CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            var found = await network.GetLocalTizenAddresses(cancellationToken, virtualScan);
            return await EnrichAsync(network, found, cancellationToken);
        }

        /// <summary>
        /// Fills in Developer-Mode info for every device that answers on the REST API. Never drops a
        /// TV: if REST is unavailable or returns nothing usable, the base entry is kept so an
        /// installable TV always stays in the list.
        /// </summary>
        public static async Task<List<NetworkDevice>> EnrichAsync(
            INetworkService network, IEnumerable<NetworkDevice> devices, CancellationToken cancellationToken = default)
        {
            // Independent per-TV lookups — run them together so a scan isn't serialized over the
            // (up to 5s) REST timeout of every TV found.
            var enriched = await Task.WhenAll(devices.Select(async device =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Enrich any TV that answers on the REST API (8001) with its friendly model/name —
                // for ready (debug-open) TVs as well as not-ready ones.
                if (!await network.IsPortOpenAsync(device.IpAddress, Constants.Ports.SamsungTvApiPort, cancellationToken))
                {
                    // No REST API — a debug-open TV reachable only over SDB. Keep it as-is (its SDB
                    // name/IP is what identifies it); Developer Mode is on since the debug port answered.
                    device.DeveloperMode = "1";
                    return device;
                }

                var samsungDevice = await ReadAsync(device, cancellationToken);
                // ReadAsync returns a fresh object, so carry over the debug-port state (installable?)
                // and the SDB name when REST didn't supply its own.
                samsungDevice.DebugPortOpen = device.DebugPortOpen;
                if (string.IsNullOrEmpty(samsungDevice.DeviceName))
                    samsungDevice.DeviceName = device.DeviceName;
                if (string.IsNullOrEmpty(samsungDevice.Manufacturer))
                    samsungDevice.Manufacturer = device.Manufacturer;
                if (string.IsNullOrEmpty(samsungDevice.MacAddress))
                    samsungDevice.MacAddress = device.MacAddress;
                return samsungDevice;
            }));

            return enriched.ToList();
        }

        /// <summary>
        /// Re-reads the Developer-Mode fields of a TV the user is about to install to and updates
        /// <paramref name="device"/> in place. The device list comes from a scan that may be minutes
        /// old, and the Developer-Mode IP is exactly what people change between scanning and
        /// installing: they see the "IP Mismatch" warning, correct the IP on the TV, and press
        /// Install again. Judged by the scan's snapshot, the guards then keep warning about an IP the
        /// TV no longer has. Call this right before <see cref="InstallGuards.Evaluate"/>.
        ///
        /// Only ever improves on the snapshot: a TV that doesn't answer keeps its scanned values, and
        /// the debug-port state is re-probed only when the scan saw it closed, so a TV restarted since
        /// then loses its stale "restart required" — a slow probe can never add one.
        /// </summary>
        public static async Task RefreshAsync(INetworkService network, NetworkDevice? device, CancellationToken cancellationToken = default)
        {
            // Placeholder picker entries ("Other…") carry no address to ask.
            if (device is null || !IPAddress.TryParse(device.IpAddress, out _))
                return;

            try
            {
                if (!device.DebugPortOpen &&
                    await IsPortOpenAsync(network, device.IpAddress, Constants.Ports.TizenDevPort, cancellationToken))
                {
                    device.DebugPortOpen = true;
                }

                if (!await IsPortOpenAsync(network, device.IpAddress, Constants.Ports.SamsungTvApiPort, cancellationToken))
                    return;

                var fresh = await ReadAsync(device, cancellationToken);

                // ReadAsync's fallback (no/invalid answer) leaves DeveloperMode blank; a real Samsung
                // answer always carries "0" or "1". Don't wipe good scan data with a failed read.
                if (string.IsNullOrEmpty(fresh.DeveloperMode))
                    return;

                if (fresh.DeveloperMode != device.DeveloperMode || fresh.DeveloperIP != device.DeveloperIP)
                {
                    Trace.WriteLine($"[TizenApi] {device.IpAddress}: Developer Mode changed since the scan " +
                                    $"(mode {device.DeveloperMode}→{fresh.DeveloperMode}, IP {device.DeveloperIP}→{fresh.DeveloperIP}).");
                }
                device.DeveloperMode = fresh.DeveloperMode;
                device.DeveloperIP = fresh.DeveloperIP;
                if (string.IsNullOrEmpty(device.DeviceName))
                    device.DeviceName = fresh.DeviceName;
                if (string.IsNullOrEmpty(device.ModelName))
                    device.ModelName = fresh.ModelName;
                if (string.IsNullOrEmpty(device.MacAddress))
                    device.MacAddress = fresh.MacAddress;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Best effort: the guards fall back to the scan's snapshot.
                Trace.WriteLine($"[TizenApi] Couldn't refresh Developer Mode info for {device.IpAddress}: {ex.Message}");
            }
        }

        // One port probe with the scan's per-probe budget, so a TV that has gone to standby since the
        // scan can't stall the Install button for the full TCP connect timeout.
        private static async Task<bool> IsPortOpenAsync(INetworkService network, string ip, int port, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(Constants.Defaults.NetworkScanTimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            return await network.IsPortOpenAsync(ip, port, linked.Token);
        }

        /// <summary>
        /// Reads <c>/api/v2/</c> for one device. Never throws: a host that doesn't answer, or answers
        /// with something that isn't the Samsung TV API, comes back as a copy of the input with the
        /// Developer-Mode fields blank.
        /// </summary>
        public static async Task<NetworkDevice> ReadAsync(NetworkDevice device, CancellationToken cancellationToken = default)
        {
            try
            {
                string url = $"http://{device.IpAddress}:{Constants.Ports.SamsungTvApiPort}/api/v2/";

                var response = await Http.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                string jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);

                // Non-Samsung devices also listen on 8001 and may answer with an HTML page
                // (a router/NAS admin UI, etc.). Skip those quietly instead of trying to parse
                // markup as JSON — otherwise every such host throws and pops an error dialog.
                var trimmed = jsonContent.TrimStart();
                if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                {
                    Trace.WriteLine($"[TizenApi] {device.IpAddress}:{Constants.Ports.SamsungTvApiPort} did not return JSON; not a Samsung TV API.");
                    return CreateFallbackDevice(device);
                }

                var deviceNode = JsonNode.Parse(jsonContent)?["device"];
                if (deviceNode == null)
                    return CreateFallbackDevice(device);

                return new NetworkDevice
                {
                    IpAddress = deviceNode["ip"]?.GetValue<string>() ?? device.IpAddress,
                    DeviceName = WebUtility.HtmlDecode(deviceNode["name"]?.GetValue<string>() ?? string.Empty),
                    ModelName = deviceNode["modelName"]?.GetValue<string>() ?? string.Empty,
                    Manufacturer = deviceNode["type"]?.GetValue<string>() ?? string.Empty,
                    DeveloperMode = deviceNode["developerMode"]?.GetValue<string>() ?? string.Empty,
                    DeveloperIP = deviceNode["developerIP"]?.GetValue<string>() ?? string.Empty,
                    // Reported for both wired and wireless sets despite the name; used for Wake-on-LAN.
                    MacAddress = deviceNode["wifiMac"]?.GetValue<string>() ?? string.Empty
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The 5s per-TV timeout, not the caller cancelling the scan.
                Trace.WriteLine($"[TizenApi] {device.IpAddress} did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                Trace.WriteLine($"[TizenApi] Error connecting to {device.IpAddress}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[TizenApi] {device.IpAddress} returned a non-JSON response: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.WriteLine($"[TizenApi] Unexpected error probing {device.IpAddress}: {ex.Message}");
            }

            return CreateFallbackDevice(device);
        }

        private static NetworkDevice CreateFallbackDevice(NetworkDevice device)
        {
            return new NetworkDevice
            {
                IpAddress = device.IpAddress,
                DeviceName = device.DeviceName,
                Manufacturer = device.Manufacturer,
                DeveloperMode = string.Empty,
                DeveloperIP = string.Empty,
                MacAddress = device.MacAddress
            };
        }
    }
}
