using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    /// <summary>One label/value pair in the TV-information view.</summary>
    public sealed record DeviceInfoRow(string Label, string Value);

    /// <summary>
    /// Everything we can report about a connected TV, gathered by
    /// <see cref="Apps2Samsung.Sdb.TizenDeviceInfoService"/> and shown by both heads' "TV information"
    /// view. Values are best-effort — anything the TV didn't report shows as "—".
    /// </summary>
    public sealed record TizenDeviceInfo(
        string IpAddress,
        string DeviceName,
        string ModelName,
        string Manufacturer,
        string Duid,
        string TizenVersion,
        string SdkToolPath,
        string DeveloperMode,
        string DeveloperIp,
        bool DebugPortOpen)
    {
        private static string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

        private string DeveloperModeDisplay => DeveloperMode?.Trim() switch
        {
            "1" => "On",
            "0" => "Off",
            _ => "—",
        };

        /// <summary>The info as ordered label/value rows, ready to render in a simple key/value list.</summary>
        public IReadOnlyList<DeviceInfoRow> Rows => new List<DeviceInfoRow>
        {
            new("TV IP", Dash(IpAddress)),
            new("Device name", Dash(DeviceName)),
            new("Model", Dash(ModelName)),
            new("Manufacturer", Dash(Manufacturer)),
            new("Tizen OS version", Dash(TizenVersion)),
            new("DUID", Dash(Duid)),
            new("Developer mode", DeveloperModeDisplay),
            new("Developer host IP", Dash(DeveloperIp)),
            new("Debug port (26101)", DebugPortOpen ? "Open" : "Closed"),
            new("SDK tool path", Dash(SdkToolPath)),
        };
    }
}
