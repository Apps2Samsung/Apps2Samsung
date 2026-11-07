using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// A live web-inspector tunnel to an app on the TV: the app was relaunched in debug mode and its
    /// inspector is reachable on <see cref="LocalPort"/> of this device. Disposing ends the tunnel
    /// (the app keeps running on the TV — debug mode only ends when the app is stopped).
    /// </summary>
    public sealed class TizenDebugSession : IAsyncDisposable
    {
        private readonly IAsyncDisposable _forward;

        internal TizenDebugSession(int localPort, int remotePort, IAsyncDisposable forward)
        {
            LocalPort = localPort;
            RemotePort = remotePort;
            _forward = forward;
        }

        /// <summary>Port on this device the inspector is tunnelled to.</summary>
        public int LocalPort { get; }

        /// <summary>Port the inspector actually listens on, on the TV.</summary>
        public int RemotePort { get; }

        public ValueTask DisposeAsync() => _forward.DisposeAsync();
    }

    /// <summary>
    /// Puts an installed app into web-inspector debug mode and tunnels the inspector back to this
    /// device, so a DevTools client can attach to it.
    ///
    /// Shared by both heads: each attaches its own <see cref="Apps2Samsung.Diagnostics.DevToolsConsole"/>
    /// to the tunnelled inspector (see <c>Apps2Samsung.Diagnostics.DevToolsInspector</c>). The
    /// desktop can additionally open the TV-hosted DevTools frontend in a browser; Chrome on Android
    /// has no <c>chrome://inspect</c> to hand off to, so the phone never does.
    /// </summary>
    public static class TizenAppDebugger
    {
        // The TV answers `0 debug <id>` with a report carrying the inspector's port, e.g.
        // "... launch_app is ... port: 43287". The number is the TV's port, not a local one.
        private static readonly Regex PortPattern = new(@"port:\s*(\d+)", RegexOptions.Compiled);

        // Tries for `0 debug` when the link keeps dying. Three is what the engine's own connect retry
        // uses, and a TV that drops three fresh connections in a row is not having a race.
        private const int DebugAttempts = 3;

        // Tizen below 4.0 knows `0 debug` only with a trailing number: `0 debug <id> 30`. Its sdbd
        // answers the bare form by closing the channel, the same "closed" a verb it does not know
        // gets, which is what a 2017 Tizen 3.0 set did to Arm TV-Store (#954). Samsung's own WITs
        // and Appium's Tizen TV driver both send the suffixed form on those sets (300 and 30); what
        // the number means is undocumented, most likely seconds to wait for the inspector client.
        // Newer sets take the bare form, which is what Tizen Studio sends them.
        public static readonly Version LegacyDebugFormBelow = new(4, 0);
        public const int LegacyDebugWait = 30;

        /// <summary>
        /// Relaunches <paramref name="tizenId"/> in debug mode and tunnels its inspector to
        /// <paramref name="localPort"/> (0 picks a free one — prefer that over a fixed port unless a
        /// specific one is needed, as Chrome's inspect page needs 9222).
        /// </summary>
        /// <remarks>
        /// The app must not be running: the TV hands out an inspector port only for the launch that
        /// `0 debug` performs itself, so callers stop the app first. That stop is why this reconnects
        /// before asking: sdbd answers a `0 was_kill` for an id it will not accept by closing the whole
        /// connection (PR #659), which leaves the pooled one dead and would make this command fail as a
        /// socket error without ever reaching the TV.
        /// </remarks>
        public static async Task<TizenDebugSession> StartAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId, int localPort = 0)
        {
            int remotePort;
            try
            {
                var (result, command) = await AskForDebugPortAsync(sdb, tvIpAddress, tizenId);
                if (result.ExitCode != 0)
                {
                    var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;

                    // Distinguished on purpose: a TV that keeps dropping the link has not said no to
                    // debug mode, it has said nothing at all, and the two want different next steps.
                    throw new InvalidOperationException(SdbTransportErrors.IsTransient(result)
                        ? $"The TV closed the connection instead of answering the request to start debug mode " +
                          $"for {tizenId} (`{command}`), on {DebugAttempts} fresh connections in a row. Its sdbd " +
                          $"does that for an id it won't launch, so this reads as the TV not running that id at " +
                          $"all rather than as a refusal it worded. Last transport error: {error}"
                        : $"The TV refused to start debug mode for {tizenId}: {error}");
                }

                var match = PortPattern.Match(result.Output ?? string.Empty);
                if (!match.Success)
                {
                    throw new InvalidOperationException(
                        $"The TV didn't report an inspector port for {tizenId}. It answered: {result.Output}");
                }

                remotePort = int.Parse(match.Groups[1].Value);
            }
            finally
            {
                // The forward below opens its own connection (see InProcessSdbEngine.ForwardAsync), so
                // dropping the pooled one here doesn't disturb the tunnel.
                await sdb.DisconnectAsync(tvIpAddress);
            }

            if (localPort == 0)
                localPort = LocalPorts.FindFree();

            Trace.WriteLine($"[debug] {tizenId} inspector on TV port {remotePort} → local {localPort}");
            var forward = await sdb.ForwardAsync(tvIpAddress, localPort, remotePort);
            return new TizenDebugSession(localPort, remotePort, forward);
        }

        /// <summary>The `0 debug` command for <paramref name="tizenId"/>, in the form this Tizen version takes.</summary>
        public static string DebugCommand(string tizenId, bool legacyForm) =>
            legacyForm ? $"0 debug {tizenId} {LegacyDebugWait}" : $"0 debug {tizenId}";

        /// <summary>Whether a TV on <paramref name="platformVersion"/> wants the suffixed `0 debug` form (below 4.0).</summary>
        public static bool UsesLegacyDebugForm(Version? platformVersion) =>
            platformVersion is not null && platformVersion < LegacyDebugFormBelow;

        /// <summary>
        /// Asks the TV for an inspector port, on a connection of this command's own. Returns the TV's
        /// answer and the command it answered.
        /// </summary>
        /// <remarks>
        /// The caller's pre-stop may have killed the pooled connection (see <see cref="StartAsync"/>),
        /// and sdbd is known to close a freshly-opened one mid-handshake right after a previous one was
        /// torn down, so the first try can die without the TV having seen the command. `0 debug` is safe
        /// to repeat — it launches an app that is meant to be launched — so it is retried on a fresh
        /// connection, and only a result that survives that is worth reporting.
        ///
        /// The form follows the TV's Tizen version (<see cref="TizenVersionCache"/>, one capability
        /// round-trip when it is not known yet). A TV whose version could not be read gets the bare
        /// form first and the suffixed one on the retries: a dropped channel is also how an old set
        /// answers the bare form, so the retry asks the other way rather than the same way again.
        /// </remarks>
        private static async Task<(ProcessResult Result, string Command)> AskForDebugPortAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId)
        {
            var version = await TizenVersionCache.ProbeAsync(sdb, tvIpAddress).ConfigureAwait(false);
            var legacyForm = UsesLegacyDebugForm(version);

            ProcessResult result;
            string command;
            for (int attempt = 1; ; attempt++)
            {
                // Drop the pooled connection first: reusing one the pre-stop may have killed is the
                // failure this guards against.
                await sdb.DisconnectAsync(tvIpAddress);

                command = DebugCommand(tizenId, legacyForm);
                result = await sdb.ShellAsync(tvIpAddress, command);
                if (attempt >= DebugAttempts || !SdbTransportErrors.IsTransient(result))
                    return (result, command);

                if (version is null && !legacyForm)
                {
                    Trace.WriteLine($"[debug] {tizenId}: link died on attempt {attempt} and this TV's Tizen version is unknown, retrying with the pre-4.0 form");
                    legacyForm = true;
                }
                else
                {
                    Trace.WriteLine($"[debug] {tizenId}: link died on attempt {attempt}, retrying");
                }
                await Task.Delay(400 * attempt);
            }
        }
    }
}
