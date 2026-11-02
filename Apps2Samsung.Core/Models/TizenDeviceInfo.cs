using System;
using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    /// <summary>One label/value pair in the TV-information view.</summary>
    public sealed record DeviceInfoRow(string Label, string Value);

    /// <summary>
    /// Everything we can report about a connected TV, gathered by
    /// <see cref="Apps2Samsung.Sdb.TizenDeviceInfoService"/> and shown by both heads' "TV information"
    /// view. Values are best-effort; anything the TV didn't report shows as "not available" in the head's language.
    /// <paramref name="LocalIp"/> is this computer's / this phone's own LAN address, listed next to the
    /// TV's Developer-host IP so a mismatch is visible at a glance. <paramref name="IsEmulator"/> is
    /// whether the "TV" is the Tizen Studio emulator on this very machine (see
    /// <see cref="Apps2Samsung.Services.TizenEmulator"/>); null when that could not be told.
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
        bool DebugPortOpen,
        string? LocalIp = null,
        bool? IsEmulator = null)
    {
        /// <summary>
        /// The info as ordered label/value rows for a simple key/value list, worded through the
        /// head's catalog (<paramref name="localize"/> takes an en.json key). A blank value shows as
        /// "not available" in that language.
        /// </summary>
        public IReadOnlyList<DeviceInfoRow> RowsFor(Func<string, string> localize)
        {
            string Or(string? s) => string.IsNullOrWhiteSpace(s) ? localize("infoNone") : s.Trim();
            var developerMode = DeveloperMode?.Trim() switch
            {
                "1" => localize("infoOn"),
                "0" => localize("infoOff"),
                _ => localize("infoNone"),
            };
            var emulator = IsEmulator switch
            {
                true => localize("infoEmulatorYes"),
                false => localize("keyNo"),
                null => localize("infoNone"),
            };
            var connectsVia = Sdb.SdbEndpoint.IsLoopback(DeveloperIp)
                ? string.Format(localize("infoConnectsRelay"), Sdb.SdbEndpoint.RelayPort(IpAddress))
                : localize("infoConnectsDirect");
            return new List<DeviceInfoRow>
            {
                new(localize("infoTvIp"), Or(IpAddress)),
                new(localize("infoDeviceName"), Or(DeviceName)),
                new(localize("infoModel"), Or(ModelName)),
                new(localize("infoManufacturer"), Or(Manufacturer)),
                new(localize("infoTizenVersion"), Or(TizenVersion)),
                new(localize("infoDuid"), Or(Duid)),
                new(localize("infoDeveloperMode"), developerMode),
                new(localize("infoDeveloperHostIp"), Or(DeveloperIp)),
                new(localize("infoLocalIp"), Or(LocalIp)),
                new(localize("infoEmulator"), emulator),
                new(localize("infoDebugPort"), localize(DebugPortOpen ? "infoPortOpen" : "infoPortClosed")),
                new(localize("infoConnectsVia"), connectsVia),
                new(localize("infoSdkToolPath"), Or(SdkToolPath)),
            };
        }
    }
}
