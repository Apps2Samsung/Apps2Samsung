using Apps2Samsung.Helpers.API;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Tizen.Devices
{
    public class DeviceHelper
    {
        private readonly INetworkService _networkService;
        private readonly TizenApiClient _tizenApiClient;

        public DeviceHelper(
            INetworkService networkService,
            TizenApiClient tizenApiClient)
        {
            _networkService = networkService;
            _tizenApiClient = tizenApiClient;
        }



        public async Task<List<NetworkDevice>> ScanForDevicesAsync(CancellationToken cancellationToken = default, bool virtualScan = false)
        {
            var devices = new List<NetworkDevice>();
            var networkDevices = await _networkService.GetLocalTizenAddresses(cancellationToken, virtualScan);

            foreach (NetworkDevice device in networkDevices)
            {
                // Check for cancellation before processing each device
                cancellationToken.ThrowIfCancellationRequested();

                // Enrich any TV that answers on the REST API (8001) with its friendly model/name —
                // for ready (debug-open) TVs as well as not-ready ones. But never drop a TV we found:
                // if REST is unavailable or returns no name, keep the base entry and fall back to the
                // SDB-resolved name / IP so an installable TV always stays in the list.
                if (await _networkService.IsPortOpenAsync(device.IpAddress, 8001, cancellationToken))
                {
                    try
                    {
                        var samsungDevice = await _tizenApiClient.GetDeveloperInfoAsync(device);
                        // GetDeveloperInfoAsync returns a fresh object, so carry over the debug-port
                        // state (installable?) and the SDB name when REST didn't supply its own.
                        samsungDevice.DebugPortOpen = device.DebugPortOpen;
                        if (string.IsNullOrEmpty(samsungDevice.DeviceName))
                            samsungDevice.DeviceName = device.DeviceName;
                        if (string.IsNullOrEmpty(samsungDevice.Manufacturer))
                            samsungDevice.Manufacturer = device.Manufacturer;
                        devices.Add(samsungDevice);
                    }
                    catch
                    {
                        Trace.WriteLine($"Failed to get developer info for device at {device.IpAddress}; keeping base entry.");
                        devices.Add(device);
                    }
                }
                else
                {
                    // No REST API — a debug-open TV reachable only over SDB. Keep it as-is (its SDB
                    // name/IP is what identifies it); Developer Mode is on since the debug port answered.
                    device.DeveloperMode = "1";
                    devices.Add(device);
                }
            }

            return devices;
        }
    }
}
