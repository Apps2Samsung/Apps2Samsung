using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Which port to dial for a TV's sdbd (TV-Store#52). sdbd lets a connection in only when the
    /// address it comes from equals Developer Mode's Host PC IP, exactly. A TV whose Host PC IP is
    /// 127.0.0.1 (a set that roots itself, or one set up for TV-Store's relay) therefore refuses
    /// every direct connection to 26101. TV-Store 0.3.14+ can run a relay on the TV that forwards a
    /// LAN port to sdbd over loopback, so sdbd sees 127.0.0.1 and accepts, whatever this device's
    /// own address is. So: Host PC IP 127.0.0.1 → dial the relay port; anything else → 26101.
    /// <para>
    /// The Host PC IP comes from the TV's REST API (port 8001, <c>developerIP</c>). A scan or the
    /// pre-install refresh hands it in through <see cref="Remember"/>; a connection to a TV nobody
    /// scanned (the CLI's <c>--tv</c>, a typed address) reads it once itself in
    /// <see cref="ResolvePortAsync"/>. Either way the answer is remembered per TV and re-read after
    /// <see cref="MaxAge"/>, since changing Host PC IP is exactly what people do between two installs.
    /// </para>
    /// </summary>
    public static class SdbEndpoint
    {
        /// <summary>The Host PC IP that means "go through the relay".</summary>
        public const string LoopbackHostIp = "127.0.0.1";

        /// <summary>sdbd's own port.</summary>
        public const int DirectPort = Constants.Ports.TizenDevPort;

        /// <summary>The port TV-Store's relay listens on unless its Settings say otherwise.</summary>
        public const int DefaultRelayPort = 26201;

        /// <summary>How long a remembered Host PC IP is trusted before the TV is asked again.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly ConcurrentDictionary<string, (string? DeveloperIp, DateTime AtUtc)> Known = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, int> RelayPorts = new(StringComparer.Ordinal);

        /// <summary>True when this Host PC IP means the TV only accepts loopback, i.e. the relay.</summary>
        public static bool IsLoopback(string? developerIp) =>
            string.Equals(developerIp?.Trim(), LoopbackHostIp, StringComparison.Ordinal);

        /// <summary>The relay port to use for this TV: the one set for it, else the default.</summary>
        public static int RelayPort(string tvIp) =>
            RelayPorts.TryGetValue(tvIp, out var port) ? port : DefaultRelayPort;

        /// <summary>A relay port other than the default for one TV (TV-Store's Settings allow one).</summary>
        public static void SetRelayPort(string tvIp, int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), port, "A port is 1 to 65535.");
            RelayPorts[tvIp] = port;
        }

        /// <summary>
        /// Records what the TV reported as its Host PC IP, from a scan or a refresh. An empty or null
        /// value (the REST API did not answer) is remembered too, so the connect does not re-ask a TV
        /// that has no REST API (the emulator) before every call.
        /// </summary>
        public static void Remember(string tvIp, string? developerIp) =>
            Known[tvIp] = (string.IsNullOrWhiteSpace(developerIp) ? null : developerIp.Trim(), DateTime.UtcNow);

        /// <summary>Forgets what was remembered about a TV, so the next connect asks it again.</summary>
        public static void Forget(string tvIp) => Known.TryRemove(tvIp, out _);

        /// <summary>
        /// Whether a connection to this TV goes through the relay, from what is remembered; false
        /// when nothing is (the connect then asks the TV, see <see cref="ResolvePortAsync"/>).
        /// </summary>
        public static bool UsesRelay(string tvIp) =>
            Known.TryGetValue(tvIp, out var known) && IsLoopback(known.DeveloperIp);

        /// <summary>The port to dial from what is remembered: the relay's when the TV's Host PC IP is 127.0.0.1, else 26101.</summary>
        public static int PortFor(string tvIp) => UsesRelay(tvIp) ? RelayPort(tvIp) : DirectPort;

        /// <summary>
        /// The port to dial, asking the TV's REST API for its Host PC IP when nothing recent is
        /// remembered. Never throws: a TV that does not answer on 8001 is dialed directly, as before.
        /// </summary>
        public static async Task<int> ResolvePortAsync(string tvIp, CancellationToken cancellationToken = default)
        {
            if (Known.TryGetValue(tvIp, out var known) && DateTime.UtcNow - known.AtUtc <= MaxAge)
                return PortFor(tvIp);

            string? developerIp = known.DeveloperIp;
            try
            {
                using var response = await Http.GetAsync($"http://{tvIp}:{Constants.Ports.SamsungTvApiPort}/api/v2/", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var device = JsonNode.Parse(json)?["device"];
                    if (device is not null)
                        developerIp = device["developerIP"]?.GetValue<string>();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // No REST API, or not a Samsung TV: keep what was known (if anything) and dial directly.
            }

            Remember(tvIp, developerIp);
            return PortFor(tvIp);
        }

        /// <summary>"192.168.1.50:26201 (TV-Store relay)" or "192.168.1.50:26101", for logs.</summary>
        public static string Describe(string tvIp, int port) =>
            port == DirectPort ? $"{tvIp}:{port}" : $"{tvIp}:{port} (TV-Store relay)";

        /// <summary>
        /// What to tell the user when the relay port did not answer: the TV only takes loopback, so
        /// the relay is the one way in, and it is off or TV-Store is too old.
        /// </summary>
        public static string RelayDownHint(string tvIp, int port) =>
            $"This TV's Developer Mode points at 127.0.0.1, so Apps2Samsung connects through TV-Store's relay on port {port}, and nothing answered there. " +
            "On the TV, open TV-Store, go to Settings and turn on \"Connect from Apps2Samsung\" (TV-Store 0.3.14 or newer); " +
            $"or set Developer Mode's Host PC IP to this device's address and restart the TV. (TV {tvIp})";

        /// <summary>The port TV-Store's own service listens on; <c>GET /relay</c> there tells what the relay last did (TV-Store 0.3.15+).</summary>
        public const int StoreServicePort = 8077;

        /// <summary>How far from now a failure the relay reports may be and still count as this one. Absolute, the TV's clock is not trusted.</summary>
        public static readonly TimeSpan MaxFailureAge = TimeSpan.FromSeconds(60);

        /// <summary>The relay's last failed connection: a code, the reason in words, when, and from which device.</summary>
        public sealed record RelayFailure(string Code, string Reason, DateTimeOffset? At, string? Client);

        /// <summary>
        /// Reads <c>GET /relay</c> from TV-Store on this TV; the body, or null on a 404 (TV-Store before
        /// 0.3.15), a timeout, or anything else. Replaceable so tests do not need a TV.
        /// </summary>
        internal static Func<string, CancellationToken, Task<string?>> ReadRelayReport { get; set; } = async (tvIp, ct) =>
        {
            try
            {
                using var response = await Http.GetAsync($"http://{tvIp}:{StoreServicePort}/relay", ct).ConfigureAwait(false);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        };

        /// <summary>
        /// The bind error from a <c>/relay</c> body that says the relay is switched on but not
        /// listening (<c>enabled: true, listening: false, error</c>); null in every other case.
        /// </summary>
        public static string? ParseRelayBindError(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var relay = JsonNode.Parse(json)?["relay"];
                if (relay?["enabled"]?.GetValue<bool>() != true || relay["listening"]?.GetValue<bool>() != false)
                    return null;
                var error = relay["error"]?.GetValue<string>();
                return string.IsNullOrWhiteSpace(error) ? null : error;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The relay's bind error for this TV, read from <c>/relay</c>; null when there is none or TV-Store cannot say.</summary>
        public static async Task<string?> RelayBindErrorAsync(string tvIp, CancellationToken cancellationToken = default) =>
            ParseRelayBindError(await ReadRelayReport(tvIp, cancellationToken).ConfigureAwait(false));

        /// <summary>The relay's last failure from a <c>/relay</c> body; null when there is none or the body is not that JSON.</summary>
        public static RelayFailure? ParseRelayFailure(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var last = JsonNode.Parse(json)?["relay"]?["lastFailure"];
                var code = last?["code"]?.GetValue<string>();
                var reason = last?["reason"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(reason))
                    return null;

                DateTimeOffset? at = DateTimeOffset.TryParse(last?["at"]?.GetValue<string>(), out var parsed) ? parsed : null;
                return new RelayFailure(code, reason, at, last?["client"]?.GetValue<string>());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// What to tell the user for a failure the relay reported, or null when it is not this one: too
        /// old, or from another device. <paramref name="localIps"/> empty means this device could not
        /// tell its own addresses, so the client is not compared.
        /// </summary>
        public static string? DescribeRelayFailure(RelayFailure failure, string tvIp, DateTimeOffset now, IReadOnlyCollection<string> localIps)
        {
            if (failure.At is { } at && (now - at).Duration() > MaxFailureAge)
                return null;
            if (!string.IsNullOrEmpty(failure.Client) && localIps.Count > 0 && !localIps.Contains(failure.Client))
                return null;

            // The allow list is edited on the TV's side only (POST /settings), so say that.
            if (string.Equals(failure.Code, "NOT_ALLOWED", StringComparison.Ordinal))
                return $"{RelayHintMarker} refused this device: it is not on the relay's allow list. Add this device to the allow list on the TV (TV-Store's screen cannot do that yet). (TV {tvIp})";

            return $"{RelayHintMarker} reports: {failure.Reason} (TV {tvIp})";
        }

        /// <summary>The relay's own reason for the failure just seen, or null when it has none (an older TV-Store, no answer, an unrelated failure).</summary>
        public static async Task<string?> RelayReasonAsync(string tvIp, CancellationToken cancellationToken = default)
        {
            var failure = ParseRelayFailure(await ReadRelayReport(tvIp, cancellationToken).ConfigureAwait(false));
            return failure is null ? null : DescribeRelayFailure(failure, tvIp, DateTimeOffset.UtcNow, LocalAddresses());
        }

        /// <summary>
        /// This device's own addresses, for telling the relay's failure for this device from another's.
        /// The .NET enumeration reports nothing useful on Android, so the mobile head replaces this with
        /// its Java-API one; an empty answer means the client is not compared.
        /// </summary>
        public static Func<IReadOnlyCollection<string>> LocalAddresses { get; set; } = DefaultLocalAddresses;

        private static IReadOnlyCollection<string> DefaultLocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address.ToString())
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>Wording both relay hints share, so a failure that already carries one is not given a second.</summary>
        public const string RelayHintMarker = "TV-Store's relay";

        /// <summary>
        /// What to tell the user when the relay accepted the connection and then dropped it: the
        /// relay is up but could not pass the request on to the TV's own sdbd, or TV-Store was closed
        /// or restarted mid-install. Unlike <see cref="RelayDownHint"/>, the relay being off is not it.
        /// </summary>
        public static string RelayDroppedHint(string tvIp, int port) =>
            $"This TV is reached through {RelayHintMarker} on port {port}, which answered but then closed the connection. " +
            "On the TV, open TV-Store, go to Settings and turn \"Connect from Apps2Samsung\" off and on again, " +
            $"keep TV-Store open (0.3.14 or newer), then try again. (TV {tvIp})";
    }
}
