using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Tells the Tizen Studio TV emulator apart from a TV. The emulator answers on the debug port
    /// like a set does, so it scans, connects and installs through the same code; the one thing
    /// that gives it away is its address, which is this machine's own. A Samsung TV is never at our
    /// own IP, so a "TV" there is the emulator running here.
    /// <para>
    /// Why it matters: the emulator checks the Samsung certificate chain exactly like a retail TV,
    /// so a distributor certificate generated for a real TV (listing that TV's DUID) fails on it
    /// with <c>[118, -12] Invalid certificate</c>. The certificate has to be generated with the
    /// emulator selected as the TV; its DUID is read over SDB like any other.
    /// </para>
    /// </summary>
    public static class TizenEmulator
    {
        /// <summary>Whether <paramref name="tvIp"/> is this machine: the loopback address, or one of
        /// <paramref name="localIps"/> (this machine's own addresses).</summary>
        public static bool IsThisMachine(string? tvIp, IEnumerable<string?> localIps)
        {
            if (string.IsNullOrWhiteSpace(tvIp))
                return false;

            var ip = tvIp.Trim();
            if (IPAddress.TryParse(ip, out var parsed) && IPAddress.IsLoopback(parsed))
                return true;

            return localIps.Any(local => !string.IsNullOrWhiteSpace(local) &&
                                         string.Equals(local.Trim(), ip, StringComparison.Ordinal));
        }

        /// <summary>As <see cref="IsThisMachine(string?, IEnumerable{string?})"/> against one local
        /// address; null when <paramref name="localIp"/> is unknown and <paramref name="tvIp"/> is
        /// not loopback, as there is nothing to compare against then.</summary>
        public static bool? IsThisMachine(string? tvIp, string? localIp)
        {
            if (IsThisMachine(tvIp, Array.Empty<string?>()))
                return true;
            if (string.IsNullOrWhiteSpace(localIp))
                return null;
            return IsThisMachine(tvIp, new[] { localIp });
        }
    }
}
