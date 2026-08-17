using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Gathers everything shown in the "TV information" view into a <see cref="TizenDeviceInfo"/>, so
    /// both heads report the same details. Reads the DUID and capabilities over SDB (works on any
    /// connected TV) and, best-effort, the Samsung REST API (port 8001) for the friendly name, model,
    /// and Developer-Mode host/IP. Every lookup is defensive — a TV that doesn't answer one source just
    /// leaves those fields blank rather than failing the whole view.
    /// </summary>
    public static class TizenDeviceInfoService
    {
        private const int SamsungTvApiPort = 8001;

        public static async Task<TizenDeviceInfo> GatherAsync(ISdbEngine sdb, string ip, bool debugPortOpen)
        {
            // ---- SDB: DUID + capabilities (Tizen version, SDK tool path) ----
            string duid = string.Empty;
            try
            {
                var d = await sdb.DuidAsync(ip);
                duid = d.Output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
                if (!TizenDuid.IsValid(duid))
                    duid = string.Empty;
            }
            catch { /* leave blank */ }

            string tizenVersion = string.Empty, sdkToolPath = string.Empty;
            try
            {
                var c = await sdb.CapabilityAsync(ip);
                var caps = TizenCapabilities.Parse(c.Output);
                tizenVersion = caps.PlatformVersion ?? string.Empty;
                sdkToolPath = caps.SdkToolPath ?? string.Empty;
            }
            catch { /* leave blank */ }

            // ---- REST /api/v2/: name, model, manufacturer, developer mode + host IP ----
            string name = string.Empty, model = string.Empty, manufacturer = string.Empty,
                   developerMode = string.Empty, developerIp = string.Empty;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = await http.GetStringAsync($"http://{ip}:{SamsungTvApiPort}/api/v2/");
                var device = JsonNode.Parse(json)?["device"];
                if (device is not null)
                {
                    name = WebUtility.HtmlDecode(device["name"]?.GetValue<string>() ?? string.Empty);
                    model = device["modelName"]?.GetValue<string>() ?? string.Empty;
                    manufacturer = device["type"]?.GetValue<string>() ?? string.Empty;
                    developerMode = device["developerMode"]?.GetValue<string>() ?? string.Empty;
                    developerIp = device["developerIP"]?.GetValue<string>() ?? string.Empty;
                }
            }
            catch { /* REST unreachable — leave those fields blank */ }

            return new TizenDeviceInfo(
                IpAddress: ip,
                DeviceName: name,
                ModelName: model,
                Manufacturer: manufacturer,
                Duid: duid,
                TizenVersion: tizenVersion,
                SdkToolPath: sdkToolPath,
                DeveloperMode: developerMode,
                DeveloperIp: developerIp,
                DebugPortOpen: debugPortOpen);
        }
    }
}
