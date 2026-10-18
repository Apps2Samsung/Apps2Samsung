using System;
using Apps2Samsung.Models;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Shared readiness classification for a detected TV, so both heads agree on when a TV can be
    /// installed to and show the same guidance when it can't. A TV that answered on the Samsung REST
    /// API (port 8001) but whose SDB debug port (26101) isn't open has Developer Mode enabled but not
    /// fully active yet — the TV must be restarted before it can be installed to.
    /// </summary>
    public static class TizenDeviceReadiness
    {
        /// <summary>
        /// True when the TV was detected via the REST API only (debug port not up), so installing
        /// can't work until it's restarted. Both heads gate the install on this and show
        /// <see cref="RestartTitle"/> / <see cref="RestartInstructions"/>.
        /// </summary>
        public static bool RequiresRestart(NetworkDevice? device) =>
            device is { DebugPortOpen: false } && !string.IsNullOrWhiteSpace(device.IpAddress);

        /// <summary>Title of the "restart the TV" prompt. The desktop head shows a localized copy
        /// (key <c>restartRequiredTitle</c>); the mobile head, which has no localization layer, uses
        /// this string directly so both heads say the same thing.</summary>
        public const string RestartTitle = "Restart the TV first";

        /// <summary>Body of the "restart the TV" prompt (desktop key <c>restartRequiredBody</c>).</summary>
        public const string RestartInstructions =
            "This TV has Developer Mode on, but its install service isn't running yet, so it can't be " +
            "installed to until it's restarted.\n\n" +
            "• Press and hold the TV's power button until it fully shuts down, then turn it back on, or\n" +
            "• Unplug the TV from power for a minute or two, then plug it back in.\n\n" +
            "After it restarts, scan again and the TV will be ready.";

        /// <summary>Why a detected TV isn't installable yet.</summary>
        public enum NotReadyReason
        {
            /// <summary>The TV reports Developer Mode off.</summary>
            DeveloperModeOff,
            /// <summary>Developer Mode points at a different machine than this one.</summary>
            DeveloperIpMismatch,
            /// <summary>Developer Mode looks right; the TV just hasn't been restarted since.</summary>
            PowerCycle,
        }

        /// <summary>
        /// Actionable reason a TV was detected but isn't installable yet (debug port closed), picked
        /// from what /api/v2/ reported. <paramref name="localIp"/> is this machine's / this phone's own
        /// address; leave it empty to skip the IP comparison.
        /// </summary>
        public static NotReadyReason WhyNotReady(NetworkDevice device, string? localIp)
        {
            if (!string.Equals(device.DeveloperMode, "1", StringComparison.Ordinal))
                return NotReadyReason.DeveloperModeOff;

            if (!string.IsNullOrEmpty(device.DeveloperIP) &&
                !string.IsNullOrEmpty(localIp) &&
                !string.Equals(device.DeveloperIP, localIp, StringComparison.Ordinal))
                return NotReadyReason.DeveloperIpMismatch;

            return NotReadyReason.PowerCycle;
        }

        /// <summary>The desktop head's localization key for a reason (its en.json copy says "this PC").</summary>
        public static string MessageKey(NotReadyReason reason) => reason switch
        {
            NotReadyReason.DeveloperModeOff => "DevNotReadyEnableDevMode",
            NotReadyReason.DeveloperIpMismatch => "DevNotReadyIpMismatch",
            _ => "DevNotReadyPowerCycle",
        };

        /// <summary>
        /// The same guidance for the mobile head, read through its localization catalog. Names the
        /// local IP when it's known, since "enter this device's IP" is the step people get wrong. The
        /// with-IP and without-IP variants are separate keys so a translator always works on a whole
        /// sentence rather than a fragment spliced into one.
        /// </summary>
        public static string Describe(NotReadyReason reason, string? localIp, Func<string, string> localize)
        {
            var hasIp = !string.IsNullOrEmpty(localIp);

            return reason switch
            {
                NotReadyReason.DeveloperModeOff => hasIp
                    ? string.Format(localize("DevNotReadyEnableDevModeDeviceIp"), localIp)
                    : localize("DevNotReadyEnableDevModeDevice"),
                NotReadyReason.DeveloperIpMismatch => hasIp
                    ? string.Format(localize("DevNotReadyIpMismatchDeviceIp"), localIp)
                    : localize("DevNotReadyIpMismatchDevice"),
                _ => localize("DevNotReadyPowerCycle"),
            };
        }
    }
}
