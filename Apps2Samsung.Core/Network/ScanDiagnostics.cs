using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apps2Samsung.Models;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// One log line per scan that says what the readiness verdict was computed from: this device's
    /// side (the configured local IP, the default-route address, every interface address) and, per
    /// TV, what <c>/api/v2/</c> reported and the reason the head would give. When a TV is shown as
    /// not ready for a local-side reason that a relaunch fixes (#922), this line is what tells a
    /// stale configured IP from a stale interface list from a TV that really points elsewhere.
    /// </summary>
    public static class ScanDiagnostics
    {
        /// <summary>
        /// Builds the line. <paramref name="configuredLocalIp"/> is the address the head compares the
        /// TV's Developer-Mode IP with (the Settings network card on the desktop, the per-TV subnet
        /// address on the phone); null or empty means the comparison is skipped, and the line says so.
        /// </summary>
        public static string Describe(
            string? configuredLocalIp,
            string? defaultRouteIp,
            IEnumerable<string> localIps,
            IEnumerable<NetworkDevice> devices)
        {
            var sb = new StringBuilder("[Scan] local side: configured=");
            sb.Append(Show(configuredLocalIp));
            sb.Append(" default-route=").Append(Show(defaultRouteIp));
            sb.Append(" interfaces=[").Append(string.Join(", ", localIps)).Append(']');

            // The verdict is the head's own diagnosis (the TV card's line), not just the debug port: a
            // TV whose Host PC IP names another machine has the port open and still refuses this one (#1079).
            var mine = localIps.ToList();
            var any = false;
            foreach (var device in devices)
            {
                any = true;
                sb.Append("; TV ").Append(device.IpAddress);
                sb.Append(": developerMode=").Append(Show(device.DeveloperMode));
                sb.Append(" developerIP=").Append(Show(device.DeveloperIP));
                sb.Append(" debugPort=").Append(device.DebugPortOpen ? "open" : "closed");
                var problem = Apps2Samsung.Design.DevModeGuide.Diagnose(device, mine);
                sb.Append(" verdict=").Append(!device.DebugPortOpen
                    ? TizenDeviceReadiness.WhyNotReady(device, configuredLocalIp).ToString()
                    : problem == Apps2Samsung.Design.DevModeProblem.None
                        ? "Ready"
                        : problem.ToString());
            }

            if (!any)
                sb.Append("; no TV found");

            return sb.ToString();
        }

        private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;
    }
}
