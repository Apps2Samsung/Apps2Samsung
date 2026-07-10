using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Desktop <see cref="IMacVendorLookup"/>: reads the OS ARP table (<c>arp</c>) to map the
    /// device IP to a MAC, then resolves the vendor via macvendors.com. Purely cosmetic — used
    /// to label a found TV during a scan. A head without ARP access (e.g. Android) simply omits
    /// this service; TV detection relies only on the open debug/REST ports, not on this.
    /// </summary>
    public class ArpMacVendorLookup : IMacVendorLookup
    {
        // Dedicated client: macvendors.com is a plain public API and must not carry the app's
        // GitHub auth header, so this does not reuse the DI-configured HttpClient.
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<string?> GetManufacturerFromIpAsync(string ipAddress)
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
    }
}
