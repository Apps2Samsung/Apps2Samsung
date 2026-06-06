using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
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
                var jsonObject = JsonNode.Parse(jsonContent);

                var logFilePath = Path.Combine(AppContext.BaseDirectory, "Logs", $"debug_tv_api_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.log");
                await File.WriteAllTextAsync(logFilePath, jsonContent);


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
                await _dialogService.ShowErrorAsync(
                    $"Error connecting to Samsung TV at {device.IpAddress}: {ex.Message}");
            }
            catch (JsonException ex)
            {
                await _dialogService.ShowErrorAsync(
                    $"Error parsing JSON response: {ex.Message}");
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(
                    $"Unexpected error: {ex.Message}");
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
