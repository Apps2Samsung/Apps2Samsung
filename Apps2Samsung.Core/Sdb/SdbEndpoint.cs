using System;
using System.Collections.Concurrent;
using System.Net.Http;
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
