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
    }
}
