using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// The Tizen version of every TV this process has talked to, by IP: what the catalog screens
    /// hold a package's <c>required_version</c> against (TV-Store#47). Filled wherever a capability
    /// report is read (device info, an install) and by <see cref="ProbeAsync"/> when a ready TV is
    /// selected. In memory only: a TV's version does not change within a session.
    /// </summary>
    public static class TizenVersionCache
    {
        private static readonly ConcurrentDictionary<string, Version> Versions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised with the TV's IP after its version became known or changed.</summary>
        public static event Action<string>? Changed;

        public static Version? TryGet(string? tvIp) =>
            !string.IsNullOrWhiteSpace(tvIp) && Versions.TryGetValue(tvIp, out var v) ? v : null;

        /// <summary>Records what a capability report said; an unparseable version is ignored.</summary>
        public static void Remember(string? tvIp, string? platformVersion)
        {
            if (string.IsNullOrWhiteSpace(tvIp) || !Version.TryParse(platformVersion, out var version))
                return;
            var known = Versions.TryGetValue(tvIp, out var old) && old == version;
            Versions[tvIp] = version;
            if (!known)
                Changed?.Invoke(tvIp);
        }

        /// <summary>
        /// One capability round-trip over SDB, remembered; failures are swallowed (a TV that is not
        /// reachable right now simply stays unknown, and the screens show no Tizen verdict for it).
        /// </summary>
        public static async Task<Version?> ProbeAsync(ISdbEngine sdb, string tvIp)
        {
            if (TryGet(tvIp) is { } known)
                return known;
            try
            {
                var result = await sdb.CapabilityAsync(tvIp).ConfigureAwait(false);
                Remember(tvIp, TizenCapabilities.Parse(result.Output).PlatformVersion);
            }
            catch
            {
                // Unknown stays unknown.
            }
            return TryGet(tvIp);
        }
    }
}
