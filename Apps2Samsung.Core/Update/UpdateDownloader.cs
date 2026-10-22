using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Update
{
    /// <summary>How far a download is: bytes done, bytes in all (-1 when the server didn't say).</summary>
    public readonly record struct DownloadProgress(long Done, long Total)
    {
        public int Percent => Total > 0 ? (int)Math.Min(100, Done * 100 / Total) : 0;
        /// <summary>"12.3 MB of 36.5 MB", or "12.3 MB" while the total is unknown.</summary>
        public string Text => Total > 0 ? $"{Megabytes(Done)} of {Megabytes(Total)}" : Megabytes(Done);
        public static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
    }

    /// <summary>
    /// Downloads a release asset to a file with progress. Its own <see cref="HttpClient"/> without
    /// the default 100-second timeout: that timeout covers the whole transfer, and an 87 MB
    /// desktop archive on a slow line takes longer than that. Cancellation is the way to stop it.
    /// </summary>
    public sealed class UpdateDownloader
    {
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        static UpdateDownloader()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Apps2Samsung");
        }

        public async Task DownloadAsync(string url, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var partial = destinationPath + ".part";
            if (File.Exists(partial))
                File.Delete(partial);

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1L;
            var done = 0L;
            progress?.Report(new DownloadProgress(0, total));

            await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                var lastReport = DateTime.MinValue;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    // At most a few reports a second: the UI can't paint faster and the log would drown.
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                    {
                        progress?.Report(new DownloadProgress(done, total));
                        lastReport = DateTime.UtcNow;
                    }
                }
            }

            if (total > 0 && done != total)
                throw new IOException($"The download ended after {done} of {total} bytes.");

            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            File.Move(partial, destinationPath);
            progress?.Report(new DownloadProgress(done, total > 0 ? total : done));
        }
    }
}
