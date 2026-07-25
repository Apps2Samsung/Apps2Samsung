using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.API
{
    public class TizenApiClient
    {
        private readonly IDialogService _dialogService;
        private readonly HttpClient _httpClient;

        public TizenApiClient(
            HttpClient httpClient,
            IDialogService dialogService)
        {
            _dialogService = dialogService;
            _httpClient = httpClient;
        }

        public async Task<NetworkDevice> GetDeveloperInfoAsync(NetworkDevice device)
        {
            try
            {
                string url = $"http://{device.IpAddress}:{Constants.Ports.SamsungTvApiPort}/api/v2/";

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string jsonContent = await response.Content.ReadAsStringAsync();

                // Non-Samsung devices also listen on 8001 and may answer with an HTML page
                // (a router/NAS admin UI, etc.). Skip those quietly instead of trying to parse
                // markup as JSON — otherwise every such host throws and pops an error dialog.
                var trimmed = jsonContent.TrimStart();
                if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                {
                    Trace.WriteLine($"[TizenApi] {device.IpAddress}:{Constants.Ports.SamsungTvApiPort} did not return JSON; not a Samsung TV API.");
                    return CreateFallbackDevice(device);
                }

                var jsonObject = JsonNode.Parse(jsonContent);

                var deviceNode = jsonObject?["device"];
                if (deviceNode == null)
                {
                    return CreateFallbackDevice(device);
                }

                return new NetworkDevice
                {
                    IpAddress = deviceNode["ip"]?.GetValue<string>() ?? device.IpAddress,
                    DeviceName = WebUtility.HtmlDecode(deviceNode["name"]?.GetValue<string>() ?? string.Empty),
                    ModelName = deviceNode["modelName"]?.GetValue<string>() ?? string.Empty,
                    Manufacturer = deviceNode["type"]?.GetValue<string>() ?? string.Empty,
                    DeveloperMode = deviceNode["developerMode"]?.GetValue<string>() ?? string.Empty,
                    DeveloperIP = deviceNode["developerIP"]?.GetValue<string>() ?? string.Empty
                };
            }
            catch (HttpRequestException ex)
            {
                Trace.WriteLine($"[TizenApi] Error connecting to {device.IpAddress}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[TizenApi] {device.IpAddress} returned a non-JSON response: {ex.Message}");
            }
            catch (Exception ex)
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
                DeveloperIP = string.Empty
            };
        }
    }
}
