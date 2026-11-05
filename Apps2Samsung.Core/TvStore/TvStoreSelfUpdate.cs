using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Remote;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>What the wait after a TV-Store self-update found (#1005).</summary>
    /// <param name="CameBack">The TV lists TV-Store at the version that was installed.</param>
    /// <param name="Version">The version the TV lists now; null when it could not be read.</param>
    /// <param name="RelayListening">Whether TV-Store's relay answered <c>listening: true</c> again after the restart.</param>
    public sealed record TvStoreRestart(bool CameBack, string? Version, bool RelayListening);

    /// <summary>
    /// Updating TV-Store through TV-Store's own relay (#1005). The push and the install command
    /// reach sdbd through the relay; the installer then stops the running TV-Store to replace it,
    /// and the relay is TV-Store's own service, so the connection dies at that moment. The head
    /// never sees <c>install completed</c>, while the install has gone through: once TV-Store is
    /// launched again it reports the new version and the relay is back. So the head, which knows
    /// both facts before it starts (the package is TV-Store, and it dials the relay port rather
    /// than 26101), says so once up front, treats the connection loss after the install command
    /// as the expected outcome, reads the new version without the developer connection (the TV's
    /// REST API), launches TV-Store again the same way and waits for its relay before anything
    /// else dials it.
    /// </summary>
    public static class TvStoreSelfUpdate
    {
        /// <summary>The notice both heads show before such an install starts, and its title (en.json keys).</summary>
        public const string NoticeTitleKey = "tvStoreSelfUpdateTitle";
        public const string NoticeKey = "tvStoreSelfUpdateBody";

        /// <summary>How long TV-Store gets to be installed and listed again after the connection dropped.</summary>
        public static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long the relay gets to come back once TV-Store is launched again.</summary>
        public static readonly TimeSpan RelayTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The pause before the first look: the TV is still replacing the package when the link dies.</summary>
        public static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(3);

        /// <summary>How often the TV is asked again while waiting.</summary>
        public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Whether a package with these ids is TV-Store: its own package id, or a copy this device
        /// installed under a new package id (#839), which its rename records map back.
        /// </summary>
        public static bool IsTvStore(string? packageId, string? appId, string? packageIdRenamesJson) =>
            Is(packageId, TvStorePackage.PackageId) ||
            (!string.IsNullOrWhiteSpace(appId) &&
             Is(PackageIdRenameStore.OriginalAppIdOf(packageIdRenamesJson, appId), TvStorePackage.AppId));

        /// <summary>
        /// Whether installing <paramref name="packagePath"/> on <paramref name="tvIp"/> is a
        /// self-update: the package is TV-Store and the TV is dialed through TV-Store's relay (its
        /// Host PC IP is 127.0.0.1). Asks the TV for its Host PC IP when nothing recent is remembered.
        /// </summary>
        public static async Task<bool> AppliesAsync(string tvIp, string packagePath, string? packageIdRenamesJson, CancellationToken cancellationToken = default)
        {
            var (appId, packageId) = await WgtManifest.ReadIdsAsync(packagePath).ConfigureAwait(false);
            if (!IsTvStore(packageId, appId, packageIdRenamesJson))
                return false;
            return await SdbEndpoint.ResolvePortAsync(tvIp, cancellationToken).ConfigureAwait(false) != SdbEndpoint.DirectPort;
        }

        /// <summary>
        /// Whether the install's result is the connection loss a self-update ends in, rather than a
        /// verdict from the TV: no install error code in the output, and either a dead-link error or
        /// no <c>install completed</c> at all (the relay closed before the TV could say it).
        /// </summary>
        public static bool IsExpectedDrop(ProcessResult? result)
        {
            if (result is null)
                return false;
            var output = result.Output ?? string.Empty;
            if (output.Contains("install failed[", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("val[fail]", StringComparison.OrdinalIgnoreCase))
                return false;
            if (TizenInstallDiagnostics.IndicatesSuccess(output))
                return false;
            return result.ExitCode == 0
                || TizenInstallDiagnostics.IsTransportLost(output)
                || SdbTransportErrors.IsTransient(result);
        }

        /// <summary>
        /// Waits for the TV to list TV-Store at <paramref name="expectedVersion"/> (any version when
        /// null), launches it again over the TV's own app launch and waits for its relay. The version
        /// comes from the TV's REST API, which needs no developer connection; once the relay is back
        /// and the REST API named no version, the developer connection's app list is read instead.
        /// <paramref name="progress"/> gets en.json keys.
        /// </summary>
        /// <param name="appId">The application id TV-Store is installed under on this TV (its own, or the renamed one).</param>
        public static async Task<TvStoreRestart> AwaitRestartAsync(
            string tvIp, string appId, string? expectedVersion, ISdbEngine? sdb, Action<string>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Invoke("statusTvStoreRestarting");
            Trace.WriteLine($"[tvstore] {tvIp}: the connection dropped while TV-Store restarts (expected); waiting for version {expectedVersion ?? "?"} under {appId}");

            await Delay(FirstLook, cancellationToken).ConfigureAwait(false);
            string? version = null;
            var deadline = Clock() + RestartTimeout;
            while (true)
            {
                version = await ReadVersion(tvIp, appId, cancellationToken).ConfigureAwait(false);
                if (Matches(version, expectedVersion) || Clock() >= deadline)
                    break;
                await Delay(Poll, cancellationToken).ConfigureAwait(false);
            }

            // The relay is TV-Store's service: it is back once TV-Store runs again, so open it the
            // way the hand-off does, without the developer connection.
            progress?.Invoke("statusOpeningTvStore");
            var launched = await Launch(tvIp, appId, cancellationToken).ConfigureAwait(false);
            Trace.WriteLine($"[tvstore] {tvIp}: launched TV-Store again: {launched}; waiting for its relay");

            progress?.Invoke("statusWaitingForRelay");
            var relay = await SdbEndpoint.WaitForRelayAsync(tvIp, RelayTimeout, untilReachable: true, cancellationToken).ConfigureAwait(false);

            // The REST API named no version (some sets answer without one): the app list over the
            // developer connection does, now that the relay is back.
            if (version is null && relay && sdb is not null)
                version = await ReadListedVersion(sdb, tvIp, appId).ConfigureAwait(false);

            var cameBack = Matches(version, expectedVersion);
            Trace.WriteLine($"[tvstore] {tvIp}: TV-Store {(cameBack ? "is back" : "did not come back")} (version {version ?? "?"}, expected {expectedVersion ?? "any"}, relay listening {relay})");
            return new TvStoreRestart(cameBack, version, relay);
        }

        /// <summary>The English failure for a TV-Store that did not come back, for the heads without a catalog of their own.</summary>
        public static string NotBackMessage(TvStoreRestart restart) =>
            $"TV-Store did not come back after the update{(restart.Version is null ? "" : $" (the TV still lists version {restart.Version})")}. " +
            "Open TV-Store on the TV: if it is there, turn on \"Connect from Apps2Samsung\" in its Settings; if it is not, install it again.";

        /// <summary>The install output a self-update ends in, in place of the <c>install completed</c> the relay could not deliver.</summary>
        public static string CompletedOutput(TvStoreRestart restart) =>
            $"install completed (TV-Store restarted at version {restart.Version})";

        private static bool Matches(string? version, string? expected) =>
            !string.IsNullOrWhiteSpace(version) &&
            (string.IsNullOrWhiteSpace(expected) || string.Equals(version.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase));

        private static async Task<string?> ReadListedVersion(ISdbEngine sdb, string tvIp, string appId)
        {
            try
            {
                var listed = await sdb.AppsAsync(tvIp).ConfigureAwait(false);
                var app = TizenInstalledApps.ParseOrThrow(listed)
                    .FirstOrDefault(a => string.Equals(a.TizenId, appId, StringComparison.OrdinalIgnoreCase));
                return string.IsNullOrWhiteSpace(app?.Version) ? null : app!.Version;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[tvstore] {tvIp}: could not read the app list after the restart: {ex.Message}");
                return null;
            }
        }

        private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        /// <summary>The TV's own word on TV-Store's version (REST); replaceable so tests need no TV.</summary>
        internal static Func<string, string, CancellationToken, Task<string?>> ReadVersion { get; set; } = SamsungRemoteApps.GetVersionAsync;

        /// <summary>Opens TV-Store on the TV without the developer connection; replaceable for tests.</summary>
        internal static Func<string, string, CancellationToken, Task<bool>> Launch { get; set; } = async (tvIp, appId, ct) =>
        {
            try
            {
                return (await SamsungRemoteApps.LaunchAsync(null, tvIp, appId, cancellationToken: ct).ConfigureAwait(false)).Succeeded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[tvstore] {tvIp}: could not launch TV-Store again: {ex.Message}");
                return false;
            }
        };

        /// <summary>The wait between two looks, and the clock the deadline is read from; tests replace both so a wait does not take real seconds.</summary>
        internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
        internal static Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    }
}
