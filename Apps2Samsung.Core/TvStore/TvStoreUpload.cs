using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.TvStore
{
    /// <summary>What TV-Store's service answered to an upload: the file waiting for OK on the TV.</summary>
    public sealed record TvStoreUploadResult(string Id, string Name, long Size, string Sha256, string? AppId, string? PackageId);

    /// <summary>TV-Store's service refused the file, or could not be reached; the message says why.</summary>
    public sealed class TvStoreUploadException : Exception
    {
        public TvStoreUploadException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// TV-Store's upload page (TV-Store #30): <c>PUT http://&lt;TV&gt;:8077/upload?name=&lt;file&gt;</c> with the
    /// package as the body. Open to the network by design; the file then waits on the TV until the
    /// person there presses Install (TV-Store signs it with its armed certificate), Not now or Discard.
    /// Nothing installs from here: a way in for a TV that SDB cannot reach (Host PC IP 127.0.0.1).
    /// </summary>
    public static class TvStoreUpload
    {
        public const int Port = 8077;
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan SendTimeout = TimeSpan.FromMinutes(5);

        private static Uri Page(string tvIp, int port, string? name = null) =>
            new($"http://{tvIp}:{port}/upload" + (name is null ? "" : "?name=" + Uri.EscapeDataString(name)));

        /// <summary>True when TV-Store's service answers its upload page (TV-Store is running on the TV).</summary>
        public static async Task<bool> IsAvailableAsync(string tvIp, CancellationToken ct = default, int port = Port)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            try
            {
                using var response = await Http.GetAsync(Page(tvIp, port), HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends <paramref name="packagePath"/> and checks that the TV got it whole (its SHA-256 against
        /// the file's). One file waits at a time: a new one replaces the one waiting.
        /// </summary>
        /// <exception cref="TvStoreUploadException">Refused (not a Tizen package, too big, an install running) or unreachable.</exception>
        public static async Task<TvStoreUploadResult> SendAsync(string tvIp, string packagePath, CancellationToken ct = default, int port = Port)
        {
            var name = Path.GetFileName(packagePath);
            string local;
            using (var f = File.OpenRead(packagePath))
                local = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false)).ToLowerInvariant();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(SendTimeout);
            JsonNode? answer;
            int status;
            try
            {
                await using var body = File.OpenRead(packagePath);
                using var content = new StreamContent(body);
                using var response = await Http.PutAsync(Page(tvIp, port, name), content, cts.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                try { answer = JsonNode.Parse(text); } catch (Exception) { answer = null; }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new TvStoreUploadException($"TV-Store did not take the file: {ex.Message}", ex);
            }

            if (answer?["ok"]?.GetValue<bool>() != true)
            {
                var why = answer?["error"]?.GetValue<string>() ?? $"HTTP {status}";
                throw new TvStoreUploadException($"TV-Store did not take the file: {why}");
            }
            var up = answer["upload"];
            var result = new TvStoreUploadResult(
                Id: up?["id"]?.GetValue<string>() ?? "",
                Name: up?["name"]?.GetValue<string>() ?? name,
                Size: up?["size"]?.GetValue<long>() ?? 0,
                Sha256: (up?["sha256"]?.GetValue<string>() ?? "").ToLowerInvariant(),
                AppId: up?["ids"]?["appId"]?.GetValue<string>(),
                PackageId: up?["ids"]?["packageId"]?.GetValue<string>());
            if (result.Sha256 != local)
                throw new TvStoreUploadException($"The file arrived damaged on the TV (SHA-256 {result.Sha256}, sent {local}). Send it again.");
            return result;
        }
    }
}
