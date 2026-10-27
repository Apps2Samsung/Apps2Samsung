using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Fetches the all-time download counts apps2samsung.com publishes (<see cref="DownloadStats"/>)
    /// and stamps them onto catalog entries (<see cref="Stamp"/>), for the "Most downloaded" group
    /// and the count in each row. Counts are decoration: any failure means no counts, never an
    /// error, and a cached copy of any age beats none (a day-old count is still the right order).
    /// Shared by every consumer in a process: the download happens once per <see cref="RefreshInterval"/>.
    /// </summary>
    public sealed class DownloadStatsService
    {
        public const string DownloadStatsUrl = "https://apps2samsung.com/catalog/downloads.json";
        public const string CacheFileName = "download-stats.cache.json";

        /// <summary>The generator runs once a day, so an hour in-process is plenty.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
        /// <summary>After a failed download the next consumer retries this much later instead.</summary>
        public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(1);

        private readonly HttpClient _http;
        private readonly string? _cachePath;
        private readonly string _url;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private DownloadStats? _current;
        private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
        private bool _fetched;

        /// <param name="http">Any client; the host needs no token.</param>
        /// <param name="cachePath">Where to keep the last good copy (null disables the disk cache).</param>
        /// <param name="url">Override for tests / a staging host.</param>
        public DownloadStatsService(HttpClient http, string? cachePath, string? url = null)
        {
            _http = http;
            _cachePath = cachePath;
            _url = url ?? DownloadStatsUrl;
        }

        /// <summary>The current counts, else the cached ones, else null. Never throws.</summary>
        public async Task<DownloadStats?> GetAsync(CancellationToken cancellationToken = default)
        {
            if (IsFresh(DateTimeOffset.UtcNow))
                return _current;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (IsFresh(now))
                    return _current;

                _current = await DownloadAsync(cancellationToken) ?? _current ?? ReadCache();
                _fetchedAt = now;
                _fetched = true;
                return _current;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool IsFresh(DateTimeOffset now)
            => _fetched && now - _fetchedAt < (_current is null ? FailureRetryInterval : RefreshInterval);

        /// <summary>
        /// Sets <see cref="GitHubRelease.Downloads"/> on the entries one provider produced: for the
        /// community bundle the sum of each entry's files (a grouped app counts all its variants),
        /// for any other provider its total, since that provider lists as one app.
        /// </summary>
        public static void Stamp(IEnumerable<GitHubRelease> entries, ProviderEntry provider, DownloadStats? stats)
        {
            if (stats is null || string.IsNullOrWhiteSpace(provider.Id) || !stats.Providers.TryGetValue(provider.Id, out var counts))
                return;

            foreach (var entry in entries)
            {
                entry.Downloads = counts.Files is null
                    ? counts.Total
                    : entry.Assets.Sum(a => counts.Files.TryGetValue(a.FileName, out var file) ? file.Total : 0);
            }
        }

        /// <summary>A count as a row shows it: 842, 4.8k, 48k, 1.2M.</summary>
        public static string Compact(long count) => count switch
        {
            < 1_000 => count.ToString(CultureInfo.CurrentCulture),
            < 10_000 => (count / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "k",
            < 1_000_000 => (count / 1_000).ToString(CultureInfo.CurrentCulture) + "k",
            _ => (count / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        };

        public static DownloadStats? Parse(string json)
        {
            try
            {
                var stats = JsonSerializer.Deserialize<DownloadStats>(json, JsonSerializerOptionsProvider.Default);
                return stats is { SchemaVersion: DownloadStats.CurrentSchemaVersion } ? stats : null;
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[DownloadStats] parse failed: {ex.Message}");
                return null;
            }
        }

        private async Task<DownloadStats?> DownloadAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                using var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var stats = Parse(json);
                if (stats is not null)
                    TryWriteCache(json);
                return stats;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DownloadStats] fetch failed: {ex.Message}");
                return null;
            }
        }

        private DownloadStats? ReadCache()
        {
            try
            {
                return !string.IsNullOrEmpty(_cachePath) && File.Exists(_cachePath) ? Parse(File.ReadAllText(_cachePath)) : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DownloadStats] cache read failed: {ex.Message}");
                return null;
            }
        }

        private void TryWriteCache(string json)
        {
            if (string.IsNullOrEmpty(_cachePath))
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                File.WriteAllText(_cachePath, json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DownloadStats] cache write failed: {ex.Message}");
            }
        }
    }
}
