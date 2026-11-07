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
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;
using Apps2Samsung.Services;

namespace Apps2Samsung.TvStore
{
    /// <summary>What TV-Store's service reports about itself, through the page's <c>A2SStore.status()</c>.</summary>
    /// <param name="Armed">True once a signing certificate has been handed over and stored on the TV.</param>
    /// <param name="OwnIp">The address the service connects to its own sdbd on; null when it could not tell.</param>
    /// <param name="SourceIds">The signed repositories TV-Store already has (#1018, TV-Store#82), by repository id; empty before TV-Store 0.3.22.</param>
    public sealed record TvStoreStatus(bool Armed, string? OwnIp, System.Collections.Generic.IReadOnlyList<string>? SourceIds = null);

    /// <summary>What the TV answered for one source sent with <see cref="TvStoreClient.AddSourcesAsync"/>.</summary>
    public sealed record TvStoreSourceResult(string Url, string? RepositoryId, bool Added, string? Name, string? Error);

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
        // How long a relay that says it is switched on but not listening yet is given before the connect.
        private static readonly TimeSpan RelayWait = TimeSpan.FromSeconds(15);

        private readonly TizenDebugSession _session;
        private readonly DevToolsConsole _console;
        private bool _disposed;

        private TvStoreClient(TizenDebugSession session, DevToolsConsole console, string version, string appId)
        {
            _session = session;
            _console = console;
            Version = version;
            AppId = appId;
        }

        /// <summary>The <c>A2SStore.version</c> the page reported.</summary>
        public string Version { get; }

        /// <summary>
        /// The application id the TV lists TV-Store under: <see cref="TvStorePackage.AppId"/>, or the id
        /// of a copy installed under a new package id (#839).
        /// </summary>
        public string AppId { get; }

        /// <summary>
        /// Starts TV-Store in debug mode and attaches to its page. <paramref name="progress"/> gets
        /// localization keys, as the debug agent reports them. Every verb addresses the id the TV
        /// lists TV-Store under, which is not its own after an install under a new package id (#839);
        /// <paramref name="packageIdRenamesJson"/> is this device's record of those renames.
        /// </summary>
        /// <exception cref="TvStoreNotInstalledException">The TV does not list TV-Store, or only an old one.</exception>
        /// <exception cref="TvStoreUnreachableException">The TV sent no app listing.</exception>
        /// <exception cref="InvalidOperationException">Debug mode was refused, the inspector never came up, or the page is not TV-Store (or an older one without the API).</exception>
        public static async Task<TvStoreClient> AttachAsync(
            ISdbEngine sdb, string tvIp, IProgress<string>? progress = null, string? packageIdRenamesJson = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            ArgumentException.ThrowIfNullOrWhiteSpace(tvIp);

            // Through TV-Store's relay (Host PC IP 127.0.0.1), the relay has to be listening before
            // anything dials it: right after an update of TV-Store itself it is still coming back
            // (#1005). An older TV-Store that cannot say is not waited for.
            if (await SdbEndpoint.ResolvePortAsync(tvIp, ct).ConfigureAwait(false) != SdbEndpoint.DirectPort)
            {
                progress?.Report("lblToolboxTvStoreWaitingRelay");
                if (!await SdbEndpoint.WaitForRelayAsync(tvIp, RelayWait, untilReachable: false, ct).ConfigureAwait(false))
                    Trace.WriteLine($"[tvstore] {tvIp}: the relay is not listening; trying anyway");
            }

            await LogNetworkAsync(tvIp, ct).ConfigureAwait(false);

            var found = await TvStorePackage.FindAsync(sdb, tvIp, packageIdRenamesJson).ConfigureAwait(false);
            if (!found.IsInstalled || found.AppId is null)
                throw new TvStoreNotInstalledException(found.State == TvStoreInstallState.Outdated);
            var appId = found.AppId;

            // Debug mode hands out an inspector port only for the launch it performs itself.
            progress?.Report("lblToolboxTvStoreStopping");
            await StopAsync(sdb, tvIp, appId, ct).ConfigureAwait(false);

            progress?.Report("lblToolboxTvStoreStarting");
            var session = await TizenAppDebugger.StartAsync(sdb, tvIp, appId).ConfigureAwait(false);

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

                Trace.WriteLine($"[tvstore] attached to {appId} v{version} on {tvIp} via local port {session.LocalPort}");
                return new TvStoreClient(session, console, version, appId);
            }
            catch
            {
                if (console is not null)
                    await console.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        // For the debug log (#1012): the TV's /api/v2/ answer (TizenDeveloperInfo logs it), this
        // device's addresses, and whether the TV's Host PC IP is the one it is reached from. Never throws.
        private static async Task LogNetworkAsync(string tvIp, CancellationToken ct)
        {
            var local = LocalAddress.Toward(tvIp);
            var all = LocalAddress.AllIpv4();
            Trace.WriteLine($"[tvstore] this device reaches {tvIp} from {local ?? "no route"}; its IPv4 addresses: {(all.Count == 0 ? "not listed" : string.Join(", ", all))}");

            var tv = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = tvIp }, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(tv.DeveloperMode))
            {
                Trace.WriteLine($"[tvstore] {tvIp} did not answer /api/v2/; Host PC IP not known");
                return;
            }
            var verdict = string.IsNullOrEmpty(tv.DeveloperIP) ? "not set"
                : string.Equals(tv.DeveloperIP, local, StringComparison.Ordinal) ? "this device"
                : tv.DeveloperIP == SdbEndpoint.LoopbackHostIp ? "TV-Store's relay"
                : "another address";
            Trace.WriteLine($"[tvstore] {tvIp} Developer Mode {tv.DeveloperMode}, Host PC IP {(string.IsNullOrEmpty(tv.DeveloperIP) ? "-" : tv.DeveloperIP)} ({verdict})");
        }

        // Stops a running TV-Store so `0 debug` can launch it. `0 was_kill` first; a retail set refuses
        // it ("wascmd cannot working in released image", 2019 Tizen 5.0 set, #881), and `0 debug` on
        // an app that is still open then never answers, which made Arm hang whenever TV-Store was on
        // screen. The TV's REST API closes it on those sets, so that is the fallback, and the app is
        // given a few seconds to be gone before the debug launch.
        private static async Task StopAsync(ISdbEngine sdb, string tvIp, string appId, CancellationToken ct)
        {
            try
            {
                var kill = await sdb.ShellAsync(tvIp, $"0 was_kill {appId}").ConfigureAwait(false);
                Trace.WriteLine($"[tvstore] was_kill: {kill.Output.Trim()}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[tvstore] was_kill failed (continuing): {ex.Message}");
            }

            if (await SamsungRemoteApps.IsRunningAsync(tvIp, appId, ct).ConfigureAwait(false) != true)
                return;

            Trace.WriteLine("[tvstore] still running after was_kill; closing it over the REST API");
            if (!await SamsungRemoteApps.TerminateAsync(tvIp, appId, ct).ConfigureAwait(false))
            {
                Trace.WriteLine("[tvstore] the REST close was refused; the debug launch may not answer");
                return;
            }

            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (await SamsungRemoteApps.IsRunningAsync(tvIp, appId, ct).ConfigureAwait(false) != true)
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
            var sources = (node["sources"] as JsonArray)?
                .Select(src => src?["repositoryId"]?.ToString())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
            return new TvStoreStatus(
                Armed: node["certReady"]?.GetValue<bool>() ?? false,
                OwnIp: node["ownIp"]?.ToString(),
                SourceIds: sources ?? new System.Collections.Generic.List<string>());
        }

        /// <summary>
        /// Hands the signing certificate to TV-Store, which stores it on the TV and signs every
        /// package it installs with it from then on. The TV then needs no head for an install.
        /// </summary>
        /// <exception cref="InvalidOperationException">The service refused or did not answer; the message carries its words.</exception>
        public Task ArmAsync(SigningCertificatePem certificate, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(certificate);
            return ArmAsync(new[] { certificate }, ct);
        }

        /// <summary>
        /// Hands every signing profile this device holds to TV-Store, the one an install on that TV
        /// would sign with first (#44). A Samsung TV only updates an app with the author certificate
        /// it was installed with, so with all of them TV-Store can update an app that any of them
        /// installed, instead of removing it. A TV-Store before 0.3.12 reads the first profile only.
        /// </summary>
        /// <exception cref="InvalidOperationException">The service refused or did not answer; the message carries its words.</exception>
        public async Task ArmAsync(IReadOnlyList<SigningCertificatePem> certificates, CancellationToken ct = default)
        {
            // What goes to the TV and what it kept, so a debug log answers "was my Partner one handed over" (#1012).
            Trace.WriteLine($"[tvstore] arming {AppId} v{Version} with {certificates.Count} certificate(s):");
            for (var i = 0; i < certificates.Count; i++)
                Trace.WriteLine("[tvstore]   " + TvStoreArmLog.Describe(certificates[i], i));
            var node = await CallAsync($"A2SStore.setCertificate({ArmPayload(certificates)})", ArmTimeout, ct).ConfigureAwait(false);
            Trace.WriteLine("[tvstore] TV-Store answered the hand-over: " + TvStoreArmLog.DescribeReply(node));
            if (node?["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"TV-Store did not take the certificate: {node?["error"]?.ToString() ?? "no reply"}");
        }

        /// <summary>How long the TV may take to answer every source: someone has to walk to it and press OK.</summary>
        private static readonly TimeSpan SourcesTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Sends signed repositories to TV-Store (#1018, TV-Store#82): each one's link, pinned id,
        /// the fingerprint the user approved here, and the key itself. The TV shows every source and
        /// asks the person at it; the reply comes once each is answered. Unsigned sources are never
        /// sent: TV-Store installs only files it can check.
        /// </summary>
        /// <exception cref="NotSupportedException">This TV-Store has no sources yet (before 0.3.22): update it.</exception>
        /// <exception cref="InvalidOperationException">The service refused or did not answer.</exception>
        public async Task<System.Collections.Generic.IReadOnlyList<TvStoreSourceResult>> AddSourcesAsync(
            System.Collections.Generic.IReadOnlyList<Apps2Samsung.Catalog.UserSource> sources, CancellationToken ct = default)
        {
            var supported = await CallAsync("typeof A2SStore.addSources === 'function'", EvaluateTimeout, ct).ConfigureAwait(false);
            if (supported?.GetValueKind() != JsonValueKind.True)
                throw new NotSupportedException("This TV-Store cannot take sources yet: update TV-Store on the TV first.");
            Trace.WriteLine($"[tvstore] sending {sources.Count} source(s) to {AppId} v{Version}: {string.Join(", ", sources.Select(src => src.RepositoryId))}");
            var node = await CallAsync($"A2SStore.addSources({SourcesPayload(sources)})", SourcesTimeout, ct).ConfigureAwait(false);
            if (node?["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"TV-Store did not take the sources: {node?["error"]?.ToString() ?? "no reply"}");
            var results = (node["results"] as JsonArray ?? new JsonArray())
                .Select(r => new TvStoreSourceResult(
                    r?["url"]?.ToString() ?? string.Empty,
                    r?["repositoryId"]?.ToString(),
                    r?["added"]?.GetValueKind() == JsonValueKind.True,
                    r?["name"]?.ToString(),
                    r?["error"]?.ToString()))
                .ToList();
            Trace.WriteLine("[tvstore] the TV answered: " + string.Join("; ", results.Select(r => $"{r.RepositoryId} {(r.Added ? "added" : r.Error)}")));
            return results;
        }

        /// <summary>The array A2SStore.addSources takes; only approved signed repositories, never an unsigned source.</summary>
        public static string SourcesPayload(System.Collections.Generic.IReadOnlyList<Apps2Samsung.Catalog.UserSource> sources)
        {
            var approved = sources.Where(Apps2Samsung.Catalog.UserSources.IsApprovedRepository).ToList();
            if (approved.Count == 0)
                throw new ArgumentException("Only signed repositories with an approved key can go to the TV.", nameof(sources));
            return JsonSerializer.Serialize(approved.Select(src => new
            {
                url = src.Url.Trim(),
                repositoryId = src.RepositoryId,
                fingerprint = Apps2Samsung.Catalog.Repositories.RepositoryVerifier.NormalizeFingerprint(src.Fingerprint!),
                name = Apps2Samsung.Catalog.UserSources.DisplayName(src),
                publicKeyPem = src.PublicKey,
            }).ToList());
        }

        /// <summary>
        /// The JSON A2SStore.setCertificate takes: the first profile's four PEM fields at the top
        /// level, as every TV-Store since 0.2.0 reads them, plus <c>certificates</c> with all of them
        /// (name included) for 0.3.12 and later.
        /// </summary>
        public static string ArmPayload(IReadOnlyList<SigningCertificatePem> certificates)
        {
            ArgumentNullException.ThrowIfNull(certificates);
            if (certificates.Count == 0)
                throw new ArgumentException("At least one certificate is needed.", nameof(certificates));
            var first = certificates[0];
            return JsonSerializer.Serialize(new
            {
                authorKeyPem = first.AuthorKeyPem,
                authorCertPem = first.AuthorCertPem,
                distributorKeyPem = first.DistributorKeyPem,
                distributorCertPem = first.DistributorCertPem,
                certificates = certificates.Select((c, i) => new
                {
                    name = string.IsNullOrWhiteSpace(c.Name) ? $"certificate {i + 1}" : c.Name,
                    authorKeyPem = c.AuthorKeyPem,
                    authorCertPem = c.AuthorCertPem,
                    distributorKeyPem = c.DistributorKeyPem,
                    distributorCertPem = c.DistributorCertPem,
                }).ToList(),
            });
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
            : base($"The TV did not list its apps ({reason}). While arming, Developer Mode's Host PC IP must be this device's current address (or 127.0.0.1 with TV-Store's relay turned on, TV-Store 0.3.14 or newer), and the TV restarted after changing it.", inner)
        {
            Reason = reason;
        }

        /// <summary>What the engine said when it could not list.</summary>
        public string Reason { get; }
    }
}
