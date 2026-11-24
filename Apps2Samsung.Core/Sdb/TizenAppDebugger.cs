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
        private readonly ISdbEngine _sdb;
        private readonly string _tvIpAddress;
        private IAsyncDisposable? _forward;
        private bool _disposed;

        internal TizenDebugSession(ISdbEngine sdb, string tvIpAddress, int localPort, int remotePort, IAsyncDisposable forward)
        {
            _sdb = sdb;
            _tvIpAddress = tvIpAddress;
            LocalPort = localPort;
            RemotePort = remotePort;
            _forward = forward;
        }

        /// <summary>Port on this device the inspector is tunnelled to.</summary>
        public int LocalPort { get; }

        /// <summary>Port the inspector actually listens on, on the TV.</summary>
        public int RemotePort { get; }

        /// <summary>Whether the tunnel is down for now (see <see cref="SuspendAsync"/>).</summary>
        public bool IsSuspended => _forward is null;

        /// <summary>
        /// Closes the tunnel and with it this session's hold on the TV's developer connection, without
        /// ending the session: the app stays in debug mode and its inspector keeps listening on
        /// <see cref="RemotePort"/>, so <see cref="ResumeAsync"/> finds it again. Whatever was attached
        /// through <see cref="LocalPort"/> is cut off and has to reattach after the resume.
        /// </summary>
        public async Task SuspendAsync()
        {
            var forward = _forward;
            _forward = null;
            if (forward is not null)
                await forward.DisposeAsync();
        }

        /// <summary>
        /// Opens the tunnel again on the same two ports. Does nothing when it is up, or when the
        /// session was disposed in the meantime (the console was closed mid hand-over).
        /// </summary>
        public async Task ResumeAsync()
        {
            if (_forward is not null || _disposed)
                return;

            Trace.WriteLine($"[debug] resuming the tunnel to TV port {RemotePort} on local {LocalPort}");
            _forward = await _sdb.ForwardAsync(_tvIpAddress, LocalPort, RemotePort);
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            var forward = _forward;
            _forward = null;
            if (forward is not null)
                await forward.DisposeAsync();
        }
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

        // How long the TV gets to report the inspector port. A set normally answers within a second
        // or two (the app's launch) and closes the channel; one that does neither left the console
        // on "attaching" for good, with nothing in the window or the log (a 2018 Tizen 4.0 set that
        // launched every app and never answered). The suffixed form gets its own wait on top, in
        // case the number is how long that TV holds the answer back.
        public static readonly TimeSpan DebugAnswerTimeout = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan LegacyDebugAnswerTimeout = TimeSpan.FromSeconds(LegacyDebugWait + 10);

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
                    if (SdbTransportErrors.IsNoAnswer(result))
                    {
                        throw new InvalidOperationException(
                            $"The TV took the request to start debug mode for {tizenId} but never reported an " +
                            $"inspector port, so there is nothing to attach to. {error}");
                    }

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
            return new TizenDebugSession(sdb, tvIpAddress, localPort, remotePort, forward);
        }

        /// <summary>
        /// Starts a second debug session (a packaged service's) next to one that is already open
        /// (the app's), stopping <paramref name="tizenId"/> first so the TV launches it itself.
        /// </summary>
        /// <remarks>
        /// The TV's sdbd takes one developer connection at a time (TV-Store#73) and the open session's
        /// tunnel is one, so a stop or a `0 debug` sent next to it arrives on a second connection that
        /// the TV closes without a word (#1140). The open session therefore gives its connection up
        /// for as long as those commands take and gets its tunnel back afterwards, whether the new
        /// session came up or not. From then on both tunnels run over one connection: the engine
        /// shares it between every forward to the same TV (see InProcessSdbEngine.ForwardAsync).
        ///
        /// The commands are deliberately not sent over the open session's own connection instead:
        /// sdbd answers an id it will not stop or launch by closing the whole connection, which would
        /// take the app's console down over a mistyped service id.
        ///
        /// Whatever was attached through <paramref name="alongside"/> is cut off by this and has to
        /// reattach once it returns or throws.
        /// </remarks>
        public static async Task<TizenDebugSession> StartAlongsideAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId, TizenDebugSession? alongside, int localPort = 0)
        {
            if (alongside is not null)
                await alongside.SuspendAsync();

            TizenDebugSession? started = null;
            try
            {
                // Debug mode only reports a port for the launch it performs itself. The id may well
                // not be running, and sdbd may drop the link over it: neither is a failure.
                try
                {
                    await sdb.ShellAsync(tvIpAddress, $"0 was_kill {tizenId}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[debug] pre-stop of {tizenId} failed (continuing): {ex.Message}");
                }

                started = await StartAsync(sdb, tvIpAddress, tizenId, localPort);
            }
            finally
            {
                if (alongside is not null)
                {
                    try
                    {
                        await alongside.ResumeAsync();
                    }
                    catch
                    {
                        if (started is not null)
                            await started.DisposeAsync();
                        throw;
                    }
                }
            }

            return started;
        }

        // The whole reply is "... port: 43287"; anything after the number is not waited for.
        private static bool ReportsPort(string reply) => PortPattern.IsMatch(reply);

        // When the bare form went unanswered and the suffixed one failed as well, the first is the
        // finding and the second the footnote: report both, as "no answer", so the user is not told
        // the TV refused an id it had in fact just launched.
        private static ProcessResult WithUnanswered(ProcessResult? unanswered, ProcessResult result, string command)
        {
            if (unanswered is null || result.ExitCode == 0 || SdbTransportErrors.IsNoAnswer(result))
                return result;

            var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            var message = $"{unanswered.Error} The pre-4.0 form (`{command}`) did not work either: {error}";
            return new ProcessResult { ExitCode = result.ExitCode, Output = message, Error = message };
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
        ///
        /// The reply is read until it names the port, not until the TV closes the channel: a TV that
        /// reports the port and keeps the channel open is answered, and one that reports nothing gets
        /// <see cref="DebugAnswerTimeout"/> instead of forever. After a bare form that went unanswered
        /// the suffixed one is tried once, whatever the version says, with the app stopped again
        /// first: the unanswered request may well have launched it, and a port is only handed out for
        /// the launch `0 debug` performs itself.
        /// </remarks>
        private static async Task<(ProcessResult Result, string Command)> AskForDebugPortAsync(
            ISdbEngine sdb, string tvIpAddress, string tizenId)
        {
            var version = await TizenVersionCache.ProbeAsync(sdb, tvIpAddress).ConfigureAwait(false);
            var legacyForm = UsesLegacyDebugForm(version);

            ProcessResult result;
            ProcessResult? unanswered = null;
            string command;
            for (int attempt = 1; ; attempt++)
            {
                // Drop the pooled connection first: reusing one the pre-stop may have killed is the
                // failure this guards against.
                await sdb.DisconnectAsync(tvIpAddress);

                command = DebugCommand(tizenId, legacyForm);
                result = await sdb.ShellUntilAsync(
                    tvIpAddress, command, ReportsPort, legacyForm ? LegacyDebugAnswerTimeout : DebugAnswerTimeout);

                if (SdbTransportErrors.IsNoAnswer(result) && !legacyForm)
                {
                    Trace.WriteLine($"[debug] {tizenId}: no answer to `{command}`, stopping the app and asking with the pre-4.0 form");
                    unanswered = result;
                    legacyForm = true;
                    attempt = 0;

                    await sdb.DisconnectAsync(tvIpAddress);
                    try
                    {
                        await sdb.ShellAsync(tvIpAddress, $"0 was_kill {tizenId}");
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[debug] stop of {tizenId} after the unanswered request failed (continuing): {ex.Message}");
                    }
                    continue;
                }

                if (attempt >= DebugAttempts || !SdbTransportErrors.IsTransient(result))
                    return (WithUnanswered(unanswered, result, command), command);

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
