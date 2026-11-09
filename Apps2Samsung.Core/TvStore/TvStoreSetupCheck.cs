using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.Remote;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>How a line of the TV-Store setup check reads: done, worth a look, blocking, or not known.</summary>
    public enum TvStoreCheckLevel { Ok, Warn, Bad, Unknown }

    /// <summary>
    /// One line of the TV-Store section's setup check (#1041): an en.json key, the values it takes,
    /// and how it reads. The heads localize it; nothing here knows a language.
    /// </summary>
    public sealed record TvStoreCheck(string Key, TvStoreCheckLevel Level, params string[] Args);

    /// <summary>What Developer Mode's Host PC IP means for TV-Store.</summary>
    public enum TvStoreHostIp
    {
        /// <summary>The TV did not say.</summary>
        Unknown,
        /// <summary>127.0.0.1: TV-Store installs over loopback, this app goes through its relay.</summary>
        Relay,
        /// <summary>The TV's own address: TV-Store installs on its own.</summary>
        TvItself,
        /// <summary>This device: arming works, TV-Store's own installs are refused.</summary>
        ThisDevice,
        /// <summary>Some other device: neither this app nor TV-Store gets in.</summary>
        Elsewhere,
    }

    /// <summary>What the TV-Store section found on the TV, read without changing anything there.</summary>
    /// <param name="State">TV-Store on the TV, from the app list or the REST API; null when neither could tell.</param>
    /// <param name="AppId">The application id the TV lists TV-Store under, when known.</param>
    /// <param name="Version">TV-Store's version as the TV's REST API reports it, when it does.</param>
    /// <param name="HostPcIp">Developer Mode's Host PC IP as the TV reports it.</param>
    /// <param name="ListingError">Why the app list could not be read, when it could not.</param>
    public sealed record TvStoreOverview(
        TvStoreInstallState? State, string? AppId, string? Version, string? HostPcIp, string? ListingError);

    /// <summary>
    /// The TV-Store section's setup check (#1041): the long "how to set up TV-Store" hint the toolbox
    /// carried, as facts this app can read. Whether TV-Store is on the TV (the app list over the
    /// developer connection, else the TV's REST API), its version, and what the Host PC IP in
    /// Developer Mode means for it. Nothing is launched and nothing is armed: whether TV-Store is
    /// armed is only known once Arm TV-Store has run.
    /// </summary>
    public static class TvStoreSetupCheck
    {
        /// <summary>What <paramref name="hostPcIp"/> means for TV-Store on <paramref name="tvIp"/>.</summary>
        /// <param name="localIps">This device's own addresses.</param>
        public static TvStoreHostIp ClassifyHostIp(string? hostPcIp, string tvIp, IReadOnlyCollection<string> localIps)
        {
            var ip = hostPcIp?.Trim();
            if (string.IsNullOrEmpty(ip))
                return TvStoreHostIp.Unknown;
            if (SdbEndpoint.IsLoopback(ip))
                return TvStoreHostIp.Relay;
            if (string.Equals(ip, tvIp?.Trim(), StringComparison.Ordinal))
                return TvStoreHostIp.TvItself;
            if (localIps.Any(l => string.Equals(l, ip, StringComparison.Ordinal)))
                return TvStoreHostIp.ThisDevice;
            return TvStoreHostIp.Elsewhere;
        }

        /// <summary>The setup check's lines for what was found, TV-Store first, then Host PC IP.</summary>
        public static IReadOnlyList<TvStoreCheck> Lines(TvStoreOverview overview, string tvIp, IReadOnlyCollection<string> localIps)
        {
            ArgumentNullException.ThrowIfNull(overview);
            var lines = new List<TvStoreCheck>
            {
                overview.State switch
                {
                    TvStoreInstallState.Installed => new TvStoreCheck("tvStoreCheckInstalled", TvStoreCheckLevel.Ok, overview.Version ?? "?"),
                    TvStoreInstallState.Outdated => new TvStoreCheck("tvStoreCheckOutdated", TvStoreCheckLevel.Bad),
                    TvStoreInstallState.NotInstalled => new TvStoreCheck("tvStoreCheckNotInstalled", TvStoreCheckLevel.Bad),
                    _ => new TvStoreCheck("tvStoreCheckUnknown", TvStoreCheckLevel.Unknown, overview.ListingError ?? string.Empty),
                },
            };

            var hostIp = overview.HostPcIp?.Trim() ?? string.Empty;
            lines.Add(ClassifyHostIp(overview.HostPcIp, tvIp, localIps) switch
            {
                TvStoreHostIp.Relay => new TvStoreCheck("tvStoreCheckHostRelay", TvStoreCheckLevel.Ok),
                TvStoreHostIp.TvItself => new TvStoreCheck("tvStoreCheckHostTv", TvStoreCheckLevel.Ok, hostIp),
                TvStoreHostIp.ThisDevice => new TvStoreCheck("tvStoreCheckHostThisDevice", TvStoreCheckLevel.Warn, hostIp),
                TvStoreHostIp.Elsewhere => new TvStoreCheck("tvStoreCheckHostElsewhere", TvStoreCheckLevel.Bad, hostIp),
                _ => new TvStoreCheck("tvStoreCheckHostUnknown", TvStoreCheckLevel.Unknown),
            });
            return lines;
        }

        /// <summary>
        /// Reads the overview from the TV. Never throws for a TV that does not answer: what could not
        /// be read stays null. <paramref name="sdb"/> may be null (no developer connection on this
        /// head); the REST API then decides whether TV-Store is there.
        /// </summary>
        public static async Task<TvStoreOverview> ReadAsync(
            string tvIp, ISdbEngine? sdb, string? packageIdRenamesJson, CancellationToken cancellationToken = default)
        {
            var hostTask = ReadHostPcIpAsync(tvIp, cancellationToken);

            TvStoreInstallState? state = null;
            string? appId = null;
            string? listingError = null;
            if (sdb is not null)
            {
                try
                {
                    var found = await TvStorePackage.FindAsync(sdb, tvIp, packageIdRenamesJson).ConfigureAwait(false);
                    state = found.State;
                    appId = found.AppId;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.WriteLine($"[tvstore] {tvIp}: setup check could not read the app list: {ex.Message}");
                    listingError = ex is TvStoreUnreachableException unreachable ? unreachable.Reason : ex.Message;
                }
            }

            // The REST API knows the version, and says whether TV-Store is there when the app list
            // could not (no developer connection, or one Host PC IP keeps out).
            var version = await SamsungRemoteApps.GetVersionAsync(tvIp, appId ?? TvStorePackage.AppId, cancellationToken).ConfigureAwait(false);
            if (state is null && version is not null)
            {
                state = TvStoreInstallState.Installed;
                appId ??= TvStorePackage.AppId;
            }

            return new TvStoreOverview(state, appId, version, await hostTask.ConfigureAwait(false), listingError);
        }

        private static async Task<string?> ReadHostPcIpAsync(string tvIp, CancellationToken cancellationToken)
        {
            var device = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = tvIp }, cancellationToken).ConfigureAwait(false);
            var ip = device.DeveloperIP?.Trim();
            if (!string.IsNullOrEmpty(ip))
                SdbEndpoint.Remember(tvIp, ip);
            return string.IsNullOrEmpty(ip) ? null : ip;
        }
    }
}
