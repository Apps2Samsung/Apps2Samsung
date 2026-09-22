using Apps2Samsung.Interfaces;
using Apps2Samsung.Sdb;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>What a service on the TV answered on one of its local HTTP endpoints.</summary>
    /// <param name="Port">The port on the TV, as asked for (not the local end of the tunnel).</param>
    /// <param name="Path">The path queried, with its leading slash.</param>
    /// <param name="StatusCode">HTTP status the service returned.</param>
    /// <param name="ReasonPhrase">The status line's text, when the service sent one.</param>
    /// <param name="ContentType">Content type the service declared, verbatim.</param>
    /// <param name="Body">The body, pretty-printed when it is JSON and with secrets masked.</param>
    /// <param name="Duration">How long the request took, tunnel included.</param>
    /// <param name="Masked">True when at least one value was masked out of <paramref name="Body"/>.</param>
    public sealed record ServiceEndpointResult(
        int Port,
        string Path,
        int StatusCode,
        string? ReasonPhrase,
        string? ContentType,
        string Body,
        TimeSpan Duration,
        bool Masked)
    {
        /// <summary>The endpoint as it reads on the TV, which is how the user thinks of it.</summary>
        public string Endpoint => $"http://127.0.0.1:{Port}{Path}";

        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    /// <summary>
    /// Queries an HTTP endpoint a packaged service exposes on the TV's own loopback address — the
    /// diagnostics endpoint an app publishes for itself, say, which is otherwise reachable only from
    /// a shell on the TV that a retail set does not give out (its sdbd answers a fixed list of
    /// <c>0 …</c> verbs, not commands).
    ///
    /// The route is the one the web inspector already takes: SDB forwards a local port to the port on
    /// the TV, and the request goes out over that tunnel, so nothing has to run on the TV but the
    /// service itself.
    /// </summary>
    public static class TizenServiceEndpoint
    {
        /// <summary>How long one request may take once the tunnel is up.</summary>
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        // The tunnel's listener is up when ForwardAsync returns, but the TV side only dials the
        // service on the first byte, and a service that has just been launched may not be listening
        // yet. A couple of quick retries turn that into a wait rather than a failure.
        private const int ConnectAttempts = 3;
        private static readonly TimeSpan ConnectDelay = TimeSpan.FromMilliseconds(400);

        // The same names the rest of the console masks (a URL's query, say), so one transcript does
        // not mask a token in one line and print it in the next.
        private static readonly Regex SecretName = new(
            SensitiveNames.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The same idea for a body that is not JSON: `name: value` / `name=value`, one per line.
        private static readonly Regex SecretAssignment = new(
            @"(?im)^(?<name>[^\r\n:=]*(?:" + SensitiveNames.Pattern + @")[^\r\n:=]*)(?<sep>\s*[:=]\s*)(?<value>\S.*)$",
            RegexOptions.Compiled);

        /// <summary>What a masked value is replaced with, in JSON and in plain text alike.</summary>
        public const string MaskedValue = SensitiveNames.MaskedValue;

        // Indented, and without the default escaping of everything outside ASCII: this JSON is read,
        // copied into an issue and exported as text — never served — so "é" and the mask's bullets
        // belong in it as themselves rather than as \u escapes.
        private static readonly JsonSerializerOptions Readable = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>
        /// GETs <paramref name="path"/> from <paramref name="port"/> on the TV's loopback address and
        /// returns what came back, ready to show: JSON is pretty-printed, and anything that reads like
        /// a credential is masked.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The port is not a TCP port number.</exception>
        /// <exception cref="HttpRequestException">
        /// Nothing answered on that port — the usual case when the service failed to start. Its
        /// message says so; the socket error it wrapped is the inner exception.
        /// </exception>
        public static async Task<ServiceEndpointResult> QueryAsync(
            ISdbEngine sdb, string tvIpAddress, int port, string path, CancellationToken ct = default)
        {
            if (port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), port, "A TCP port is 1-65535.");

            path = NormalizePath(path);
            var localPort = LocalPorts.FindFree();
            Trace.WriteLine($"[service] {tvIpAddress} 127.0.0.1:{port}{path} → local {localPort}");

            var forward = await sdb.ForwardAsync(tvIpAddress, localPort, port);
            try
            {
                using var http = new HttpClient { Timeout = RequestTimeout };
                var address = $"http://127.0.0.1:{localPort}{path}";
                var started = Stopwatch.StartNew();

                using var response = await GetWithRetryAsync(http, address, port, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                var elapsed = started.Elapsed;

                var (text, masked) = Mask(Prettify(body));
                return new ServiceEndpointResult(
                    port,
                    path,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    response.Content.Headers.ContentType?.ToString(),
                    text,
                    elapsed,
                    masked);
            }
            finally
            {
                await forward.DisposeAsync();
            }
        }

        // A forwarded port cannot report a refusal, and the raw socket error hides that. sdb accepts
        // the connection at this end and only dials the port on the TV once the first byte goes out,
        // so a port with nothing behind it comes back as the tunnel being torn down - "Connection
        // reset by peer", the same wording a dying SDB link produces. Passed through as-is it reads
        // as this tool breaking rather than as an answer: the reporter in #545 was asked to tell a
        // refusal from a reset on two ports, which is a distinction this route can never show, and
        // both came back identical. Say what it means instead.
        private static async Task<HttpResponseMessage> GetWithRetryAsync(
            HttpClient http, string address, int port, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await http.GetAsync(address, ct);
                }
                catch (HttpRequestException) when (attempt < ConnectAttempts)
                {
                    await Task.Delay(ConnectDelay, ct);
                }
                catch (HttpRequestException ex)
                {
                    throw new HttpRequestException(
                        $"Nothing is listening on port {port} on the TV: {ConnectAttempts} attempts " +
                        $"over the forward were closed without an answer. A forwarded port cannot " +
                        $"report a refusal, so this is what a service that never started looks like. " +
                        $"Socket error: {ex.Message}", ex);
                }
            }
        }

        /// <summary>Normalises a user-typed path: blank means the root, and the leading slash is ours to add.</summary>
        public static string NormalizePath(string? path)
        {
            var trimmed = path?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return "/";
            return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
        }

        /// <summary>Re-renders a JSON body indented; anything else comes back untouched.</summary>
        public static string Prettify(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;

            try
            {
                var node = JsonNode.Parse(body);
                return node?.ToJsonString(Readable) ?? body;
            }
            catch (JsonException)
            {
                return body;
            }
        }

        /// <summary>
        /// Masks values whose name reads like a credential, so a diagnostics dump can be copied into an
        /// issue without a token going with it. Returns the text and whether anything was masked, which
        /// is what lets the caller say so rather than leave the user guessing at the dots.
        /// </summary>
        public static (string Text, bool Masked) Mask(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return (body, false);

            JsonNode? node = null;
            try { node = JsonNode.Parse(body); }
            catch (JsonException) { /* not JSON; fall through to the line-based pass */ }

            if (node is not null)
            {
                var masked = MaskNode(node);
                return (node.ToJsonString(Readable), masked);
            }

            var replaced = false;
            var text = SecretAssignment.Replace(body, match =>
            {
                replaced = true;
                return match.Groups["name"].Value + match.Groups["sep"].Value + MaskedValue;
            });
            return (text, replaced);
        }

        // Walks the tree in place. A matching name is masked whatever its value is — an object under
        // "credentials" is as much of a leak as a string under "token".
        private static bool MaskNode(JsonNode node)
        {
            var masked = false;

            switch (node)
            {
                case JsonObject obj:
                    // The names are taken first: the values are replaced as we go, and a JsonObject
                    // cannot be enumerated while it is being written to.
                    foreach (var name in obj.Select(property => property.Key).ToList())
                    {
                        if (SecretName.IsMatch(name))
                        {
                            if (obj[name] is not null)
                            {
                                obj[name] = JsonValue.Create(MaskedValue);
                                masked = true;
                            }
                            continue;
                        }

                        if (obj[name] is JsonNode child)
                            masked |= MaskNode(child);
                    }
                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonNode child)
                            masked |= MaskNode(child);
                    }
                    break;
            }

            return masked;
        }
    }
}
