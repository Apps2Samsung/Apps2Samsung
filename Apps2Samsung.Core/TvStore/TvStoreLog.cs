using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>
    /// TV-Store's own log, pulled from the TV for a bug report. TV-Store's service serves it to the
    /// whole LAN at <c>http://&lt;TV IP&gt;:8077/log.txt</c> (this run plus the previous one, no keys),
    /// so no Developer Mode and no token are needed; TV-Store only has to be open on the TV, because
    /// its service runs while the app does.
    /// </summary>
    public static class TvStoreLog
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        /// <summary>The address the log is served at.</summary>
        public static string UrlFor(string tvIp, int port = SdbEndpoint.StoreServicePort) => $"http://{tvIp}:{port}/log.txt";

        /// <summary>
        /// The file name a pulled log is saved under: <c>tvstore_&lt;TV IP&gt;_&lt;local time&gt;.log</c>,
        /// so it sorts next to the debug logs and says which TV it came from.
        /// </summary>
        public static string FileNameFor(string tvIp, DateTime localTime) =>
            $"tvstore_{tvIp.Replace(':', '-')}_{localTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.log";

        /// <summary>Downloads the log text.</summary>
        /// <exception cref="TvStoreLogUnavailableException">TV-Store did not answer, answered with an error, or sent nothing.</exception>
        public static async Task<string> FetchAsync(string tvIp, CancellationToken ct = default, int port = SdbEndpoint.StoreServicePort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tvIp);
            string text;
            try
            {
                using var response = await Http.GetAsync(UrlFor(tvIp, port), ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new TvStoreLogUnavailableException(tvIp, $"HTTP {(int)response.StatusCode}");
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TvStoreLogUnavailableException(tvIp, "no answer in time");
            }
            catch (HttpRequestException ex)
            {
                throw new TvStoreLogUnavailableException(tvIp, ex.Message, ex);
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new TvStoreLogUnavailableException(tvIp, "the log is empty");
            return text;
        }

        /// <summary>
        /// Downloads the log and writes it to <paramref name="directory"/> (made when missing) under
        /// <see cref="FileNameFor"/>. Returns the file's path.
        /// </summary>
        /// <exception cref="TvStoreLogUnavailableException">TV-Store did not answer, answered with an error, or sent nothing.</exception>
        public static Task<string> SaveAsync(string tvIp, string directory, CancellationToken ct = default, int port = SdbEndpoint.StoreServicePort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            return SaveToFileAsync(tvIp, Path.Combine(directory, FileNameFor(tvIp, DateTime.Now)), ct, port);
        }

        /// <summary>
        /// Downloads the log and writes it to <paramref name="path"/> (its folder made when missing),
        /// with one header line saying where and when it came from. Returns the full path.
        /// </summary>
        /// <exception cref="TvStoreLogUnavailableException">TV-Store did not answer, answered with an error, or sent nothing.</exception>
        public static async Task<string> SaveToFileAsync(string tvIp, string path, CancellationToken ct = default, int port = SdbEndpoint.StoreServicePort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var text = await FetchAsync(tvIp, ct, port).ConfigureAwait(false);
            path = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var header = $"# TV-Store log from {tvIp}, fetched by Apps2Samsung {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({UrlFor(tvIp, port)})\n";
            await File.WriteAllTextAsync(path, header + text, new UTF8Encoding(false), ct).ConfigureAwait(false);
            Trace.WriteLine($"[tvstore] saved TV-Store's log from {tvIp} ({text.Length:N0} characters) to {path}");
            return path;
        }
    }

    /// <summary>TV-Store's log could not be fetched. Nearly always: TV-Store is not open on the TV, or the IP is not the TV's.</summary>
    public sealed class TvStoreLogUnavailableException : Exception
    {
        public TvStoreLogUnavailableException(string tvIp, string reason, Exception? inner = null)
            : base($"TV-Store did not hand over its log from {tvIp} ({reason}). Open TV-Store on the TV and try again.", inner)
        {
            TvIp = tvIp;
            Reason = reason;
        }

        public string TvIp { get; }

        /// <summary>What went wrong, in a few words, for the heads' own localized message.</summary>
        public string Reason { get; }
    }
}
