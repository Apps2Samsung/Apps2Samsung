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
                var result = await AskForDebugPortAsync(sdb, tvIpAddress, tizenId);
                if (result.ExitCode != 0)
                {
                    var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;

                    // Distinguished on purpose: a TV that keeps dropping the link has not said no to
                    // debug mode, it has said nothing at all, and the two want different next steps.
                    throw new InvalidOperationException(SdbTransportErrors.IsTransient(result)
                        ? $"The TV closed the connection instead of answering the request to start debug mode " +
                          $"for {tizenId}, on {DebugAttempts} fresh connections in a row. Its sdbd does that for " +
                          $"an id it won't launch, so this reads as the TV not running that id at all rather " +
                          $"than as a refusal it worded. Last transport error: {error}"
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

        /// <summary>
        /// Asks the TV for an inspector port, on a connection of this command's own.
        /// </summary>
        /// <remarks>
        /// The caller's pre-stop may have killed the pooled connection (see <see cref="StartAsync"/>),
        /// and sdbd is known to close a freshly-opened one mid-handshake right after a previous one was
        /// torn down, so the first try can die without the TV having seen the command. `0 debug` is safe
        /// to repeat — it launches an app that is meant to be launched — so it is retried on a fresh
        /// connection, and only a result that survives that is worth reporting.
        /// </remarks>
        private static async Task<ProcessResult> AskForDebugPortAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId)
        {
            ProcessResult result;
            for (int attempt = 1; ; attempt++)
            {
                // Drop the pooled connection first: reusing one the pre-stop may have killed is the
                // failure this guards against.
                await sdb.DisconnectAsync(tvIpAddress);

                result = await sdb.ShellAsync(tvIpAddress, $"0 debug {tizenId}");
                if (attempt >= DebugAttempts || !SdbTransportErrors.IsTransient(result))
                    return result;

                Trace.WriteLine($"[debug] {tizenId}: link died on attempt {attempt}, retrying");
                await Task.Delay(400 * attempt);
            }
        }
    }
}
