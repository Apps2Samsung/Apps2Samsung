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
