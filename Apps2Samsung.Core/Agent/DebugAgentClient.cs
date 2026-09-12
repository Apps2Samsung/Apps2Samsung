using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Diagnostics;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Agent
{
    /// <summary>One installed app as the agent's <c>tizen.application.getAppsInfo()</c> reports it.</summary>
    /// <param name="Show">
    /// False for an app the platform hides from its launcher. On a hospitality set this is the real
    /// hidden set — as against the ids the toolbox guesses at.
    /// </param>
    public sealed record DebugAgentApp(
        string Id,
        string Name,
        string PackageId,
        string Version,
        bool Show,
        string? IconPath)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }

    /// <summary>One running app, from <c>tizen.application.getAppsContext()</c>.</summary>
    public sealed record DebugAgentContext(string AppId, string ContextId);

    /// <summary>What the agent knows about the set it runs on.</summary>
    public sealed record DebugAgentPlatform(string Agent, string? Tizen, string? Model, string? Firmware, string UserAgent);

    /// <summary>How a launch through the agent ended.</summary>
    public enum DebugAgentLaunchState
    {
        /// <summary>The platform took the launch and an app context for the id appeared afterwards.</summary>
        Launched,

        /// <summary>
        /// The platform took the launch, but no context for the id showed up. Either it exited at once,
        /// or the hotel launcher keeps it off-screen — the TV's screen is the tie-breaker.
        /// </summary>
        LaunchedNoContext,

        /// <summary>The platform's error callback fired; <see cref="DebugAgentLaunchResult.ErrorName"/> says why.</summary>
        Refused,

        /// <summary>
        /// The agent stopped answering after the launch. The usual cause is another app taking the
        /// screen and the platform suspending the agent — which, read the right way, is a launch that
        /// worked. Check the TV, then attach again.
        /// </summary>
        AgentUnresponsive,
    }

    /// <summary>The agent's verdict on one launch, verbatim where the platform gave one.</summary>
    public sealed record DebugAgentLaunchResult(
        DebugAgentLaunchState State,
        string? ErrorName,
        string? ErrorMessage,
        IReadOnlyList<DebugAgentContext> Running)
    {
        public bool Accepted => State is DebugAgentLaunchState.Launched or DebugAgentLaunchState.LaunchedNoContext;
    }

    /// <summary>The agent is attached but did not answer an evaluation in time.</summary>
    public sealed class DebugAgentUnresponsiveException : Exception
    {
        public DebugAgentUnresponsiveException(string message) : base(message) { }
    }

    /// <summary>
    /// The desktop and mobile end of the Apps2Samsung Debug agent: a sideloaded web app on the TV
    /// that launches other apps from the inside, with <c>tizen.application.launch()</c>, and reports
    /// what the platform answered (tizen-community-packages#34).
    /// <para>
    /// Why an agent at all: every route this app already has to a TV goes through the Smart Hub
    /// launcher — the remote channel's deep link, the REST endpoint, and SDB's <c>was_execute</c>,
    /// which resolves an id against the Smart Hub app database and answers <c>launch failed[400]</c>
    /// for anything not in it. The TV's own menus (the hotel menu, the factory menu, the settings
    /// app, the store) are not in it, on any set. A sideloaded app <em>is</em>, and from inside one
    /// the platform's own application manager is reachable. So: install the agent like any package,
    /// launch it in debug mode (<see cref="TizenAppDebugger"/>), attach over the DevTools protocol
    /// (<see cref="DevToolsConsole"/>) and evaluate calls on its <c>window.A2S</c> object.
    /// </para>
    /// <para>
    /// Everything the agent returns is JSON; nothing it returns is trusted beyond that. A call that
    /// gets no answer within <see cref="EvaluateTimeout"/> means the agent is suspended or gone —
    /// after a launch, that is itself information (see <see cref="DebugAgentLaunchState.AgentUnresponsive"/>).
    /// </para>
    /// </summary>
    public sealed class DebugAgentClient : IAsyncDisposable
    {
        /// <summary>The operation a plain app-control launch uses when nothing better is known.</summary>
        public const string MainOperation = "http://tizen.org/appcontrol/operation/main";

        /// <summary>How many operations the agent's <c>appControls()</c> probes — mirrors its OPERATIONS list.</summary>
        public const int ProbedOperationCount = 25;

        /// <summary>How long one evaluation may take before the agent counts as unresponsive.</summary>
        public static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(8);

        // After launch() reports success the platform needs a moment to create the app's context.
        private static readonly TimeSpan ContextSettleDelay = TimeSpan.FromMilliseconds(1200);

        private readonly ISdbEngine _sdb;
        private readonly string _tvIp;
        private readonly TizenDebugSession _session;
        private readonly DevToolsConsole _console;
        private bool _disposed;

        private DebugAgentClient(ISdbEngine sdb, string tvIp, TizenDebugSession session, DevToolsConsole console, string agentVersion)
        {
            _sdb = sdb;
            _tvIp = tvIp;
            _session = session;
            _console = console;
            AgentVersion = agentVersion;
            _console.Disconnected += reason => Disconnected?.Invoke(reason);
        }

        /// <summary>The <c>A2S.version</c> the running agent reported.</summary>
        public string AgentVersion { get; }

        /// <summary>Raised once when the inspector connection ends, off the UI thread.</summary>
        public event Action<string?>? Disconnected;

        /// <summary>
        /// Whether the TV lists the agent. Read off <c>vd_applist</c>, the same listing the installed-apps
        /// view uses — the remote channel's query never answers on the sets this exists for.
        /// </summary>
        public static async Task<bool> IsInstalledAsync(ISdbEngine sdb, string tvIp)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            var listed = await sdb.AppsAsync(tvIp).ConfigureAwait(false);
            return TizenInstalledApps.Parse(listed.Output)
                .Any(a => string.Equals(a.TizenId, DebugAgentPackage.AppId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Starts the agent in debug mode and attaches to it. <paramref name="progress"/> receives
        /// localization keys, the way <c>RemoteSession</c> reports, so each head shows them in its
        /// own language.
        /// </summary>
        /// <exception cref="InvalidOperationException">The TV refused debug mode, the inspector never came up, or what answered is not the agent.</exception>
        public static async Task<DebugAgentClient> AttachAsync(
            ISdbEngine sdb, string tvIp, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            ArgumentException.ThrowIfNullOrWhiteSpace(tvIp);

            // Debug mode hands out an inspector port only for the launch it performs itself, so a
            // running agent has to go first. It may well not be running — that is not a failure.
            progress?.Report("lblToolboxAgentStopping");
            try
            {
                await sdb.ShellAsync(tvIp, $"0 was_kill {DebugAgentPackage.AppId}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[agent] pre-stop failed (continuing): {ex.Message}");
            }

            progress?.Report("lblToolboxAgentStarting");
            var session = await TizenAppDebugger.StartAsync(sdb, tvIp, DebugAgentPackage.AppId).ConfigureAwait(false);

            DevToolsConsole? console = null;
            try
            {
                progress?.Report("lblToolboxAgentConnecting");
                var targets = await DevToolsInspector.ListTargetsAsync(session.LocalPort, ct).ConfigureAwait(false);

                // The agent is one page. Prefer the one that says so, in case the inspector also lists
                // a service worker or a leftover target from a previous debug session.
                var target = targets.FirstOrDefault(t => t.Url.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                             ?? targets[0];

                console = new DevToolsConsole();
                await console.ConnectAsync(target.WebSocketUrl, ct).ConfigureAwait(false);

                var version = await EvaluateWithTimeoutAsync(console, "window.A2S && window.A2S.version", ct).ConfigureAwait(false);
                // `false` when there is no A2S object on the page at all, a string when there is.
                var agentVersion = Str(version);
                if (string.IsNullOrWhiteSpace(agentVersion))
                {
                    throw new InvalidOperationException(
                        $"The page on the TV's inspector is not the Apps2Samsung Debug agent ({target.Title}: {target.Url}).");
                }

                Trace.WriteLine($"[agent] attached to {DebugAgentPackage.AppId} v{agentVersion} on {tvIp} via local port {session.LocalPort}");
                return new DebugAgentClient(sdb, tvIp, session, console, agentVersion!);
            }
            catch
            {
                if (console is not null)
                    await console.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Platform version, model and firmware as the agent sees them.</summary>
        public async Task<DebugAgentPlatform> PlatformAsync(CancellationToken ct = default)
        {
            var node = await CallAsync("A2S.platform()", ct).ConfigureAwait(false);
            return new DebugAgentPlatform(
                Agent: Str(node?["agent"]) ?? AgentVersion,
                Tizen: Str(node?["tizen"]),
                Model: Str(node?["model"]),
                Firmware: Str(node?["firmware"]),
                UserAgent: Str(node?["userAgent"]) ?? string.Empty);
        }

        /// <summary>Every app installed on the TV, hidden ones included, sorted by name.</summary>
        public async Task<IReadOnlyList<DebugAgentApp>> ListAppsAsync(CancellationToken ct = default)
        {
            var node = await CallAsync("A2S.apps()", ct).ConfigureAwait(false);
            if (node is not JsonArray items)
                return Array.Empty<DebugAgentApp>();

            return items
                .Select(item => new DebugAgentApp(
                    Id: Str(item?["id"]) ?? string.Empty,
                    Name: Str(item?["name"]) ?? string.Empty,
                    PackageId: Str(item?["packageId"]) ?? string.Empty,
                    Version: Str(item?["version"]) ?? string.Empty,
                    Show: item?["show"]?.GetValue<bool>() ?? true,
                    IconPath: NullIfBlank(Str(item?["iconPath"]))))
                .Where(a => a.Id.Length > 0)
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>The app contexts the platform currently has — what is really running.</summary>
        public async Task<IReadOnlyList<DebugAgentContext>> RunningAsync(CancellationToken ct = default)
        {
            var node = await CallAsync("A2S.running()", ct).ConfigureAwait(false);
            if (node is not JsonArray items)
                return Array.Empty<DebugAgentContext>();

            return items
                .Select(item => new DebugAgentContext(Str(item?["appId"]) ?? string.Empty, Str(item?["id"]) ?? string.Empty))
                .Where(c => c.AppId.Length > 0)
                .ToList();
        }

        /// <summary>
        /// The app control operations <paramref name="appId"/> registers for, out of the list the agent
        /// probes with <c>findAppControl()</c>. Empty means none of them — which is what a bare
        /// <see cref="LaunchAsync"/> refusal plus an empty list adds up to: no entry point from a user app.
        /// A non-empty list is the operation to hand <see cref="LaunchControlAsync"/>.
        /// </summary>
        public async Task<IReadOnlyList<string>> ProbeAppControlsAsync(string appId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);

            // Twenty-five sequential IPC round trips on the TV: well past the single-call timeout.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            JsonNode? node;
            try
            {
                node = await _console.EvaluateValueAsync($"A2S.appControls({Js(appId)})", timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unresponsive();
            }

            return (node?["operations"] as JsonArray)?
                .Select(Str)
                .Where(op => !string.IsNullOrEmpty(op))
                .Select(op => op!)
                .ToList()
                ?? new List<string>();
        }

        /// <summary><c>tizen.application.launch(id)</c>, then a look at the app contexts.</summary>
        public Task<DebugAgentLaunchResult> LaunchAsync(string appId, CancellationToken ct = default) =>
            LaunchCoreAsync(appId, $"A2S.launch({Js(appId)})", ct);

        /// <summary>
        /// <c>tizen.application.launchAppControl()</c> aimed at <paramref name="appId"/> explicitly.
        /// A different entry point into the same app: some apps register for an operation and refuse
        /// a bare launch, or the other way round.
        /// </summary>
        public Task<DebugAgentLaunchResult> LaunchControlAsync(
            string appId, string operation = MainOperation, CancellationToken ct = default) =>
            LaunchCoreAsync(appId, $"A2S.launchControl({Js(appId)}, {Js(operation)})", ct);

        private async Task<DebugAgentLaunchResult> LaunchCoreAsync(string appId, string expression, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);

            JsonNode? outcome;
            try
            {
                // A Promise that settles in the platform's success/error callback; the inspector
                // awaits it, so this is the verdict and not the call returning.
                outcome = await CallAsync(expression, ct).ConfigureAwait(false);
            }
            catch (DebugAgentUnresponsiveException)
            {
                return new DebugAgentLaunchResult(DebugAgentLaunchState.AgentUnresponsive, null, null, Array.Empty<DebugAgentContext>());
            }

            if (outcome?["ok"]?.GetValue<bool>() != true)
            {
                return new DebugAgentLaunchResult(
                    DebugAgentLaunchState.Refused,
                    Str(outcome?["name"]) ?? "Error",
                    Str(outcome?["message"]) ?? (outcome is null ? "no answer from the platform" : outcome.ToJsonString()),
                    Array.Empty<DebugAgentContext>());
            }

            // Accepted. Whether the app is now up is a separate question the context list answers —
            // unless the launched app took the screen and the agent got suspended with it.
            try
            {
                await Task.Delay(ContextSettleDelay, ct).ConfigureAwait(false);
                var running = await RunningAsync(ct).ConfigureAwait(false);
                var appeared = running.Any(c => string.Equals(c.AppId, appId, StringComparison.OrdinalIgnoreCase));
                return new DebugAgentLaunchResult(
                    appeared ? DebugAgentLaunchState.Launched : DebugAgentLaunchState.LaunchedNoContext,
                    null, null, running);
            }
            catch (DebugAgentUnresponsiveException)
            {
                return new DebugAgentLaunchResult(DebugAgentLaunchState.AgentUnresponsive, null, null, Array.Empty<DebugAgentContext>());
            }
        }

        /// <summary>
        /// Any expression, rendered the way a console shows it — for the ad-hoc box in the toolbox,
        /// where the next thing worth trying on a set is not yet a button.
        /// </summary>
        public async Task<string> EvaluateAsync(string expression, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expression);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EvaluateTimeout);
            try
            {
                return await _console.EvaluateAsync(expression, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unresponsive();
            }
        }

        // The agent's functions return plain values and never throw; an exception here is therefore
        // the inspector's (a syntax slip in the expression) and worth surfacing as such.
        private async Task<JsonNode?> CallAsync(string expression, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await EvaluateWithTimeoutAsync(_console, expression, ct).ConfigureAwait(false);
        }

        private static async Task<JsonNode?> EvaluateWithTimeoutAsync(DevToolsConsole console, string expression, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(EvaluateTimeout);
            try
            {
                return await console.EvaluateValueAsync(expression, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Unresponsive();
            }
        }

        private static DebugAgentUnresponsiveException Unresponsive() => new(
            $"The debug agent did not answer within {EvaluateTimeout.TotalSeconds:0} s. " +
            "If another app has just taken the screen, the platform has suspended the agent — attach again to continue.");

        // A JSON string literal is a valid JavaScript string literal, escapes and all.
        private static string Js(string value) => JsonSerializer.Serialize(value);

        private static string? Str(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>
        /// Drops the inspector connection and the tunnel, and stops the agent: it has no business
        /// staying on screen once the toolbox is done with it. An app it launched is unaffected.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { await _console.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[agent] console teardown: {ex.Message}"); }

            try { await _session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[agent] tunnel teardown: {ex.Message}"); }

            try { await _sdb.ShellAsync(_tvIp, $"0 was_kill {DebugAgentPackage.AppId}").ConfigureAwait(false); }
            catch (Exception ex) { Trace.WriteLine($"[agent] stop after detach failed: {ex.Message}"); }
        }
    }
}
