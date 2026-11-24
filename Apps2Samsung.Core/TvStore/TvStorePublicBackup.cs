using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>
    /// The backup without keys that TV-Store's service hands to a device on the LAN by itself
    /// (TV-Store#149): <c>GET http://&lt;TV IP&gt;:8077/backup/public</c>. No debug attach and nobody
    /// at the TV, so the automatic copy of a TV's app list (#1177) can carry TV-Store's part as it is
    /// now, not as it was when somebody last took a backup by hand.
    /// <para>
    /// <c>GET /relay</c> says <c>backupPublic: true</c> on a TV-Store that has the route; an older one
    /// answers 403 to it, the same as to a device that is not allowed, so the flag is read first.
    /// The route answers 409 (<c>NO_SCREEN_STATE</c>) until TV-Store was opened once on the TV.
    /// Every outcome but a backup is "nothing this time".
    /// </para>
    /// </summary>
    public static class TvStorePublicBackup
    {
        // Short: this runs each time a TV's list is written, and a TV-Store that is not running
        // must not hold the copy up.
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

        /// <summary>The address the backup is served at.</summary>
        public static string UrlFor(string tvIp, int port = SdbEndpoint.StoreServicePort) => $"http://{tvIp}:{port}/backup/public";

        /// <summary>
        /// Reads <c>GET /backup/public</c>: the status and the body, or null when there was no answer.
        /// Replaceable so tests do not need a TV.
        /// </summary>
        internal static Func<string, CancellationToken, Task<(int Status, string? Body)?>> ReadBackup { get; set; } = DefaultReadBackup;

        /// <summary>Reads <c>GET /relay</c>, as the relay check does. Replaceable for the same reason.</summary>
        internal static Func<string, CancellationToken, Task<string?>> ReadRelay { get; set; } = DefaultReadRelay;

        private static Task<string?> DefaultReadRelay(string tvIp, CancellationToken ct) => SdbEndpoint.ReadRelayReport(tvIp, ct);

        /// <summary>For tests: no TV answers.</summary>
        internal static void Silence()
        {
            ReadRelay = (_, _) => Task.FromResult<string?>(null);
            ReadBackup = (_, _) => Task.FromResult<(int Status, string? Body)?>(null);
        }

        internal static void ResetForTests()
        {
            ReadRelay = DefaultReadRelay;
            ReadBackup = DefaultReadBackup;
        }

        private static async Task<(int Status, string? Body)?> DefaultReadBackup(string tvIp, CancellationToken ct)
        {
            try
            {
                using var response = await Http.GetAsync(UrlFor(tvIp), ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ((int)response.StatusCode, body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a <c>/relay</c> body says this TV-Store has the route.</summary>
        public static bool SaysSupported(string? relayJson)
        {
            if (string.IsNullOrWhiteSpace(relayJson))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(relayJson);
                return doc.RootElement.ValueKind == JsonValueKind.Object &&
                       doc.RootElement.TryGetProperty("backupPublic", out var flag) && flag.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// The backup out of an answer of the route; null for anything but a 200 with a TV-Store backup
        /// that holds no keys. One that says it holds them is refused: this route never sends one, and
        /// the copies it goes into are written without a question.
        /// </summary>
        public static JsonElement? Parse(int status, string? body)
        {
            if (status != (int)HttpStatusCode.OK || string.IsNullOrWhiteSpace(body))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var backup = doc.RootElement.Clone();
                return TvStoreBackup.Describe(backup) is { HasSecrets: false } ? backup : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Asks the TV-Store on <paramref name="tvIp"/> for its backup without keys; null when it has none to give.</summary>
        public static async Task<JsonElement?> FetchAsync(string tvIp, CancellationToken ct = default)
        {
            // A saved list can be keyed by something that is no address (one imported from a file).
            if (string.IsNullOrWhiteSpace(tvIp) || !IPAddress.TryParse(tvIp.Trim(), out _))
                return null;
            tvIp = tvIp.Trim();

            if (!SaysSupported(await ReadRelay(tvIp, ct).ConfigureAwait(false)))
                return null;
            if (await ReadBackup(tvIp, ct).ConfigureAwait(false) is not { } answer)
                return null;

            var backup = Parse(answer.Status, answer.Body);
            if (backup is null)
                Trace.WriteLine($"[TvStoreBackup] TV-Store on {tvIp} gave no backup without keys: HTTP {answer.Status}{Reason(answer.Body)}");
            return backup;
        }

        /// <summary>
        /// <see cref="FetchAsync"/> for a caller off the UI thread that cannot await, within a few
        /// seconds at most. Never throws.
        /// </summary>
        public static JsonElement? TryFetch(string tvIp)
        {
            try
            {
                using var limit = new CancellationTokenSource(Budget);
                return Task.Run(() => FetchAsync(tvIp, limit.Token), limit.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvStoreBackup] Asking TV-Store on {tvIp} for its backup failed: {ex.Message}");
                return null;
            }
        }

        // The code of a refusal ("NO_SCREEN_STATE"), for the log.
        private static string Reason(string? body)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                        return " " + code.GetString();
                }
            }
            catch (JsonException)
            {
            }
            return string.Empty;
        }
    }
}
