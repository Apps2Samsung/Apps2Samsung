using Apps2Samsung.Interfaces;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    /// Shared by both heads because only the last step differs: the desktop hands the local port to
    /// Chrome's <c>chrome://inspect</c>, while the mobile head speaks the DevTools protocol itself
    /// (see <c>Apps2Samsung.Diagnostics.DevToolsInspector</c>) — Chrome on Android has no
    /// <c>chrome://inspect</c> to hand off to.
    /// </summary>
    public static class TizenAppDebugger
    {
        // The TV answers `0 debug <id>` with a report carrying the inspector's port, e.g.
        // "... launch_app is ... port: 43287". The number is the TV's port, not a local one.
        private static readonly Regex PortPattern = new(@"port:\s*(\d+)", RegexOptions.Compiled);

        /// <summary>
        /// Relaunches <paramref name="tizenId"/> in debug mode and tunnels its inspector to
        /// <paramref name="localPort"/> (0 picks a free one — prefer that over a fixed port unless a
        /// specific one is needed, as Chrome's inspect page needs 9222).
        /// </summary>
        /// <remarks>
        /// The app must not be running: the TV hands out an inspector port only for the launch that
        /// `0 debug` performs itself, so callers stop the app first.
        /// </remarks>
        public static async Task<TizenDebugSession> StartAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId, int localPort = 0)
        {
            int remotePort;
            try
            {
                var result = await sdb.ShellAsync(tvIpAddress, $"0 debug {tizenId}");
                if (result.ExitCode != 0)
                {
                    var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                    throw new InvalidOperationException(
                        $"The TV refused to start debug mode for {tizenId}: {error}");
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
                localPort = FindFreeLocalPort();

            Trace.WriteLine($"[debug] {tizenId} inspector on TV port {remotePort} → local {localPort}");
            var forward = await sdb.ForwardAsync(tvIpAddress, localPort, remotePort);
            return new TizenDebugSession(localPort, remotePort, forward);
        }

        // Ask the OS for an unused port by binding port 0 and reading back what it assigned. There is
        // an unavoidable race between releasing it here and the tunnel claiming it, but the
        // alternative — a hardcoded port — collides far more often on a phone, where nothing
        // guarantees a well-known debug port is free.
        private static int FindFreeLocalPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
