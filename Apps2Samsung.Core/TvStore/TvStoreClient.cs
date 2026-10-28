using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Certificate;
using Apps2Samsung.Diagnostics;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Remote;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>What TV-Store's service reports about itself, through the page's <c>A2SStore.status()</c>.</summary>
    /// <param name="Armed">True once a signing certificate has been handed over and stored on the TV.</param>
    /// <param name="OwnIp">The address the service connects to its own sdbd on; null when it could not tell.</param>
    public sealed record TvStoreStatus(bool Armed, string? OwnIp);

    /// <summary>
    /// A head's end of the TV-Store app (#844): the way to hand it the signing certificate. TV-Store's
    /// service only listens on the TV's loopback, so a head cannot post to it; the page can, and the
    /// page is reachable the way the debug agent is: start the app in debug mode
    /// (<see cref="TizenAppDebugger"/>), attach over the DevTools protocol (<see cref="DevToolsConsole"/>)
    /// and call its <c>window.A2SStore</c>. Every call there resolves with <c>{ ok, ... }</c>.
    /// </summary>
    public sealed class TvStoreClient : IAsyncDisposable
    {
        /// <summary>The first TV-Store page with <c>window.A2SStore</c>.</summary>
        public static readonly Version HandOverSince = new(0, 2, 0);

        private static readonly TimeSpan EvaluateTimeout = TimeSpan.FromSeconds(8);
        // The certificate post carries a few kilobytes and the service writes a file: still quick,
        // but the service may first have to start, which the page does on load.
        private static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(20);

        private readonly TizenDebugSession _session;
        private readonly DevToolsConsole _console;
        private bool _disposed;

        private TvStoreClient(TizenDebugSession session, DevToolsConsole console, string version)
        {
            _session = session;
            _console = console;
            Version = version;
        }

        /// <summary>The <c>A2SStore.version</c> the page reported.</summary>
        public string Version { get; }

        /// <summary>
        /// Starts TV-Store in debug mode and attaches to its page. <paramref name="progress"/> gets
        /// localization keys, as the debug agent reports them.
        /// </summary>
        /// <exception cref="TvStoreNotInstalledException">The TV does not list TV-Store, or only an old one.</exception>
        /// <exception cref="TvStoreUnreachableException">The TV sent no app listing.</exception>
        /// <exception cref="InvalidOperationException">Debug mode was refused, the inspector never came up, or the page is not TV-Store (or an older one without the API).</exception>
        public static async Task<TvStoreClient> AttachAsync(
            ISdbEngine sdb, string tvIp, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            ArgumentException.ThrowIfNullOrWhiteSpace(tvIp);

            var state = await TvStorePackage.GetInstallStateAsync(sdb, tvIp).ConfigureAwait(false);
            if (state != TvStoreInstallState.Installed)
                throw new TvStoreNotInstalledException(state == TvStoreInstallState.Outdated);

            // Debug mode hands out an inspector port only for the launch it performs itself.
            progress?.Report("lblToolboxTvStoreStopping");
            await StopAsync(sdb, tvIp, ct).ConfigureAwait(false);

            progress?.Report("lblToolboxTvStoreStarting");
            var session = await TizenAppDebugger.StartAsync(sdb, tvIp, TvStorePackage.AppId).ConfigureAwait(false);

            DevToolsConsole? console = null;
            try
            {
                progress?.Report("lblToolboxTvStoreConnecting");
                var targets = await DevToolsInspector.ListTargetsAsync(session.LocalPort, ct).ConfigureAwait(false);
                var target = targets.FirstOrDefault(t => t.Url.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                             ?? targets[0];

                console = new DevToolsConsole();
                await console.ConnectAsync(target.WebSocketUrl, ct).ConfigureAwait(false);

                var node = await EvaluateAsync(console, "window.A2SStore && window.A2SStore.version", EvaluateTimeout, ct).ConfigureAwait(false);
                var version = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new InvalidOperationException(
                        $"The page on the TV's inspector is not TV-Store, or a TV-Store older than {HandOverSince} without the hand-over API ({target.Title}: {target.Url}).");
                }

                Trace.WriteLine($"[tvstore] attached to {TvStorePackage.AppId} v{version} on {tvIp} via local port {session.LocalPort}");
                return new TvStoreClient(session, console, version);
            }
            catch
            {
                if (console is not null)
                    await console.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        // Stops a running TV-Store so `0 debug` can launch it. `0 was_kill` first; a retail set refuses
        // it ("wascmd cannot working in released image", 2019 Tizen 5.0 set, #881), and `0 debug` on
        // an app that is still open then never answers, which made Arm hang whenever TV-Store was on
        // screen. The TV's REST API closes it on those sets, so that is the fallback, and the app is
        // given a few seconds to be gone before the debug launch.
        private static async Task StopAsync(ISdbEngine sdb, string tvIp, CancellationToken ct)
        {
            try
            {
                var kill = await sdb.ShellAsync(tvIp, $"0 was_kill {TvStorePackage.AppId}").ConfigureAwait(false);
                Trace.WriteLine($"[tvstore] was_kill: {kill.Output.Trim()}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[tvstore] was_kill failed (continuing): {ex.Message}");
            }

            if (await SamsungRemoteApps.IsRunningAsync(tvIp, TvStorePackage.AppId, ct).ConfigureAwait(false) != true)
                return;

            Trace.WriteLine("[tvstore] still running after was_kill; closing it over the REST API");
            if (!await SamsungRemoteApps.TerminateAsync(tvIp, TvStorePackage.AppId, ct).ConfigureAwait(false))
            {
                Trace.WriteLine("[tvstore] the REST close was refused; the debug launch may not answer");
                return;
            }

            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (await SamsungRemoteApps.IsRunningAsync(tvIp, TvStorePackage.AppId, ct).ConfigureAwait(false) != true)
                    return;
            }
            Trace.WriteLine("[tvstore] TV-Store still reports running 5 s after the REST close");
        }

        /// <summary>The service's own status: whether a certificate is stored, and the IP it uses.</summary>
        /// <exception cref="InvalidOperationException">The page could not reach its service.</exception>
        public async Task<TvStoreStatus> StatusAsync(CancellationToken ct = default)
        {
            var node = await CallAsync("A2SStore.status()", EvaluateTimeout, ct).ConfigureAwait(false);
            if (node?["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"TV-Store's service did not answer: {node?["error"]?.ToString() ?? "no reply"}");
            return new TvStoreStatus(
                Armed: node["certReady"]?.GetValue<bool>() ?? false,
                OwnIp: node["ownIp"]?.ToString());
        }

        /// <summary>
        /// Hands the signing certificate to TV-Store, which stores it on the TV and signs every
        /// package it installs with it from then on. The TV then needs no head for an install.
        /// </summary>
        /// <exception cref="InvalidOperationException">The service refused or did not answer; the message carries its words.</exception>
        public async Task ArmAsync(SigningCertificatePem certificate, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            var payload = JsonSerializer.Serialize(new
            {
                authorKeyPem = certificate.AuthorKeyPem,
                authorCertPem = certificate.AuthorCertPem,
                distributorKeyPem = certificate.DistributorKeyPem,
                distributorCertPem = certificate.DistributorCertPem,
            });
            var node = await CallAsync($"A2SStore.setCertificate({payload})", ArmTimeout, ct).ConfigureAwait(false);
            if (node?["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"TV-Store did not take the certificate: {node?["error"]?.ToString() ?? "no reply"}");
        }

        private async Task<JsonNode?> CallAsync(string expression, TimeSpan timeout, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await EvaluateAsync(_console, expression, timeout, ct).ConfigureAwait(false);
        }

        private static async Task<JsonNode?> EvaluateAsync(DevToolsConsole console, string expression, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await console.EvaluateValueAsync(expression, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"TV-Store did not answer within {timeout.TotalSeconds:0}s.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _console.DisposeAsync().ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>TV-Store is not on the TV. It is installed through a head like any package.</summary>
    public sealed class TvStoreNotInstalledException : Exception
    {
        public TvStoreNotInstalledException(bool outdated = false)
            : base(outdated
                ? $"An old TV-Store ({TvStorePackage.LegacyAppId}) is installed on this TV. Update it from Apps2Samsung first ({TvStorePackage.RepositoryUrl})."
                : $"TV-Store ({TvStorePackage.PackageId}) is not installed on this TV. Install it from Apps2Samsung first ({TvStorePackage.RepositoryUrl}).")
        {
            IsOutdated = outdated;
        }

        /// <summary>The TV lists only the pre-0.3.5 <see cref="TvStorePackage.LegacyAppId"/>.</summary>
        public bool IsOutdated { get; }
    }

    /// <summary>
    /// The TV sent no app listing, so whether TV-Store is there is unknown (#889). Almost always the
    /// set's sdbd refusing this device: Developer Mode off, or its Host PC IP not this device's
    /// current address, or changed without restarting the TV.
    /// </summary>
    public sealed class TvStoreUnreachableException : Exception
    {
        public TvStoreUnreachableException(string reason, Exception? inner = null)
            : base($"The TV did not list its apps ({reason}). While arming, Developer Mode's Host PC IP must be this device's current address, and the TV restarted after changing it.", inner)
        {
            Reason = reason;
        }

        /// <summary>What the engine said when it could not list.</summary>
        public string Reason { get; }
    }
}
