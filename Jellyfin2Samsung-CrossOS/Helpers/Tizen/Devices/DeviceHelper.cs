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

                if (await _networkService.IsPortOpenAsync(device.IpAddress, 8001, cancellationToken))
                {
                    try
                    {
                        var samsungDevice = await _tizenApiClient.GetDeveloperInfoAsync(device);
                        if (!string.IsNullOrEmpty(samsungDevice.DeviceName))
                            devices.Add(samsungDevice);
                    }
                    catch
                    {
                        Trace.WriteLine($"Failed to get developer info for device at {device.IpAddress}.");
                    }
                }
                else
                {
                    try
                    {
                        device.ModelName = device.ModelName;
                        device.Manufacturer = device.Manufacturer;
                        device.DeveloperMode = "1";
                        device.DeveloperIP = string.Empty;

                        devices.Add(device);
                    }
                    catch { }
                }
            }

            return devices;
        }
    }
}
