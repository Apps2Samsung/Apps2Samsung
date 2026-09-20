using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>One inspectable page on the TV, as advertised by the web inspector's HTTP endpoint.</summary>
    /// <param name="Title">The page's document title, for picking between targets.</param>
    /// <param name="Url">The page's URL (for a .wgt, a <c>file://</c> path into the package).</param>
    /// <param name="WebSocketUrl">
    /// Where to attach the DevTools protocol, already rewritten to the local end of the tunnel.
    /// </param>
    /// <param name="FrontendUrl">
    /// The TV-hosted DevTools frontend for this page (the inspector serves it next to the protocol
    /// socket), likewise re-pointed at the local end of the tunnel. Opening it in a Chromium-based
    /// browser gives the full DevTools without going through <c>chrome://inspect</c>.
    /// </param>
    public sealed record DevToolsTarget(string Title, string Url, Uri WebSocketUrl, Uri FrontendUrl);

    /// <summary>
    /// Discovers what can be inspected over a <see cref="Apps2Samsung.Sdb.TizenDebugSession"/>.
    ///
    /// The inspector serves a small HTTP API next to the protocol socket; <c>/json</c> lists the open
    /// pages and, for each, the WebSocket URL to attach to.
    /// </summary>
    public static class DevToolsInspector
    {
        /// <summary>
        /// Lists the inspectable pages on the local end of the tunnel, retrying while the inspector
        /// comes up — the TV advertises nothing for a moment after the app is relaunched.
        /// </summary>
        public static async Task<IReadOnlyList<DevToolsTarget>> ListTargetsAsync(
            int localPort, CancellationToken ct = default)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var endpoint = $"http://127.0.0.1:{localPort}/json";

            Exception? lastError = null;
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var json = await http.GetStringAsync(endpoint, ct);
                    var targets = Parse(json, localPort);
                    if (targets.Count > 0)
                        return targets;

                    // Reachable but still empty: the page hasn't registered yet.
                    lastError = new InvalidOperationException("The inspector listed no pages.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }

            throw new InvalidOperationException(
                $"The app's inspector never came up on port {localPort}. {lastError?.Message}", lastError);
        }

        /// <summary>
        /// Turns an inspector <c>/json</c> body into targets addressed at the local end of the tunnel.
        /// Split out from the fetch so the parsing is testable without a TV.
        /// </summary>
        public static IReadOnlyList<DevToolsTarget> Parse(string json, int localPort)
        {
            var targets = new List<DevToolsTarget>();

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return targets;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var socketPath = SocketPath(entry);
                if (socketPath is null)
                    continue;

                targets.Add(new DevToolsTarget(
                    Text(entry, "title") ?? "(untitled)",
                    Text(entry, "url") ?? string.Empty,
                    new Uri($"ws://127.0.0.1:{localPort}{socketPath}"),
                    FrontendUrl(entry, socketPath, localPort)));
            }

            return targets;
        }

        // The inspector reports webSocketDebuggerUrl with ITS OWN host and port — the TV's — because it
        // has no idea it is being tunnelled. Attaching to that address would either fail or, worse,
        // reach something else on the local network, so only the path is kept and re-pointed at our end
        // of the tunnel.
        private static string? SocketPath(JsonElement entry)
        {
            var advertised = Text(entry, "webSocketDebuggerUrl");
            if (!string.IsNullOrWhiteSpace(advertised) &&
                Uri.TryCreate(advertised, UriKind.Absolute, out var parsed))
            {
                return parsed.PathAndQuery;
            }

            // No URL advertised (some firmware omits it for the already-attached page); the id is
            // enough to build the conventional path.
            var id = Text(entry, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                Trace.WriteLine($"[devtools] no webSocketDebuggerUrl for target {id}; assuming /devtools/page/{id}");
                return $"/devtools/page/{id}";
            }

            return null;
        }

        // devtoolsFrontendUrl is "/devtools/inspector.html?ws=<tv-host>:<port>/devtools/page/<id>" —
        // a relative path whose ws= parameter again names the TV's own address. Keep the path the
        // firmware chose (it knows where its frontend lives) but point the socket at our tunnel end.
        // Firmware that omits it gets the conventional path, which is what every observed TV sends.
        private static Uri FrontendUrl(JsonElement entry, string socketPath, int localPort)
        {
            var local = $"127.0.0.1:{localPort}";
            var advertised = Text(entry, "devtoolsFrontendUrl");

            string pathAndQuery;
            if (!string.IsNullOrWhiteSpace(advertised) &&
                Uri.TryCreate(advertised, UriKind.RelativeOrAbsolute, out var parsed))
            {
                pathAndQuery = parsed.IsAbsoluteUri ? parsed.PathAndQuery : advertised;
                var wsAt = pathAndQuery.IndexOf("ws=", StringComparison.OrdinalIgnoreCase);
                if (wsAt >= 0)
                {
                    // Replace whatever host:port follows ws= (up to the socket path) with ours.
                    var hostStart = wsAt + 3;
                    var hostEnd = pathAndQuery.IndexOf('/', hostStart);
                    if (hostEnd < 0) hostEnd = pathAndQuery.Length;
                    pathAndQuery = pathAndQuery[..hostStart] + local + pathAndQuery[hostEnd..];
                }
            }
            else
            {
                pathAndQuery = $"/devtools/inspector.html?ws={local}{socketPath}";
            }

            if (!pathAndQuery.StartsWith('/'))
                pathAndQuery = "/" + pathAndQuery;

            return new Uri($"http://{local}{pathAndQuery}");
        }

        private static string? Text(JsonElement entry, string property) =>
            entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
