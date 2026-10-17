using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Fetches the pre-resolved app catalog apps2samsung.com publishes (<see cref="ResolvedCatalog"/>),
    /// so the heads can list every provider's releases without touching <c>api.github.com</c>.
    /// A snapshot is <em>usable</em> when it parses, its schema is one this build understands and it
    /// is younger than <see cref="MaxAge"/>; anything else is treated as "no snapshot" and the caller
    /// takes the live-API path it always had. The last usable download is kept on disk as well, the
    /// way <c>ProviderManifestService</c> keeps the manifest, as a last resort for when both the
    /// snapshot host and the GitHub API are unreachable (<see cref="GetCachedAsync"/>).
    /// Shared by every consumer in a process: the download happens once per <see cref="RefreshInterval"/>.
    /// </summary>
    public sealed class ResolvedCatalogService
    {
        public const string ResolvedCatalogUrl = "https://apps2samsung.com/catalog/resolved.json";
        public const string CacheFileName = "resolved-catalog.cache.json";

        /// <summary>Older than this and the snapshot is ignored: the generator runs every 30 minutes, so a day-old file means it is broken.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
        /// <summary>How long one download is reused in-process before the next consumer re-fetches.</summary>
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
        /// <summary>After a failed download (host unreachable, bad file) the next consumer retries this much later instead.</summary>
        public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(1);

        private readonly HttpClient _http;
        private readonly string? _cachePath;
        private readonly string _url;
        private readonly Func<DateTimeOffset> _clock;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private ResolvedCatalog? _current;
        private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
        private bool _fetched;

        /// <param name="http">Any client; the snapshot host needs no token. A User-Agent is set per request.</param>
        /// <param name="cachePath">Where to keep the last usable copy (null disables the disk cache). On macOS this must not be inside the .app bundle (#498).</param>
        /// <param name="url">Override for tests / a staging host.</param>
        /// <param name="clock">Override for tests.</param>
        public ResolvedCatalogService(HttpClient http, string? cachePath, string? url = null, Func<DateTimeOffset>? clock = null)
        {
            _http = http;
            _cachePath = cachePath;
            _url = url ?? ResolvedCatalogUrl;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// The current usable snapshot, or null when there is none (unreachable, stale, unknown
        /// schema, unparsable). Never throws. Concurrent callers share one download.
        /// </summary>
        public async Task<ResolvedCatalog?> GetAsync(CancellationToken cancellationToken = default)
        {
            var now = _clock();
            if (IsFresh(now))
                return IsUsable(_current, now) ? _current : null;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                now = _clock();
                if (IsFresh(now))
                    return IsUsable(_current, now) ? _current : null;

                _current = await DownloadAsync(cancellationToken);
                _fetchedAt = now;
                _fetched = true;
                return IsUsable(_current, now) ? _current : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        // Is the in-memory result still current? A successful download lives RefreshInterval, a
        // failed one only FailureRetryInterval, so a host that was down at startup is tried again soon.
        private bool IsFresh(DateTimeOffset now)
            => _fetched && now - _fetchedAt < (_current is null ? FailureRetryInterval : RefreshInterval);

        /// <summary>
        /// The last snapshot this machine downloaded, however old, for when the live API has failed
        /// as well: stale release data beats an empty list. Null when nothing was ever cached.
        /// </summary>
        public async Task<ResolvedCatalog?> GetCachedAsync()
        {
            if (_current is not null)
                return _current;
            if (string.IsNullOrEmpty(_cachePath))
                return null;
            try
            {
                if (!File.Exists(_cachePath))
                    return null;
                var json = await File.ReadAllTextAsync(_cachePath);
                return Parse(json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ResolvedCatalog] cache read failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The snapshot entry for a manifest provider: matched by <c>id</c> when the provider has one,
        /// else by URL. Only an <c>ok</c> entry counts; a provider the generator could not resolve
        /// returns null so the caller fetches it live.
        /// </summary>
        public static ResolvedProvider? Find(ResolvedCatalog? snapshot, ProviderEntry provider)
            => Find(snapshot, provider.Id, provider.Url);

        public static ResolvedProvider? Find(ResolvedCatalog? snapshot, string? id, string? url)
        {
            if (snapshot is null)
                return null;

            ResolvedProvider? match = null;
            if (!string.IsNullOrWhiteSpace(id))
                match = snapshot.Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (match is null && !string.IsNullOrWhiteSpace(url))
                match = snapshot.Providers.FirstOrDefault(p => string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase));

            return match is { Ok: true } ? match : null;
        }

        /// <summary>
        /// Deep-copies a snapshot provider's releases, trimmed to <paramref name="take"/>. Copies,
        /// because the heads stamp <c>Category</c>/<c>RequiresPartner</c>/<c>Variant</c> onto the
        /// objects they list and the shared snapshot instance must not carry one load's stamps
        /// into the next.
        /// </summary>
        public static List<GitHubRelease> CopyReleases(ResolvedProvider provider, int take)
        {
            if (take < 1) take = 1;
            var source = provider.Releases.Count > take ? provider.Releases.GetRange(0, take) : provider.Releases;
            var json = JsonSerializer.Serialize(source, JsonSerializerOptionsProvider.Default);
            return JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default)
                ?? new List<GitHubRelease>();
        }

        public static bool IsUsable(ResolvedCatalog? snapshot, DateTimeOffset now)
            => snapshot is not null
               && snapshot.SchemaVersion == ResolvedCatalog.CurrentSchemaVersion
               && snapshot.GeneratedAt != default
               && now - snapshot.GeneratedAt < MaxAge
               // Tolerate a little clock skew between the runner and the client, nothing more.
               && snapshot.GeneratedAt - now < TimeSpan.FromHours(1);

        public static ResolvedCatalog? Parse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<ResolvedCatalog>(json, JsonSerializerOptionsProvider.Default);
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[ResolvedCatalog] parse failed: {ex.Message}");
                return null;
            }
        }

        private async Task<ResolvedCatalog?> DownloadAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.UserAgent.ParseAdd("Apps2Samsung");
                using var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var snapshot = Parse(json);
                if (snapshot is null)
                    return null;

                if (IsUsable(snapshot, _clock()))
                    TryWriteCache(json);
                else
                    Trace.WriteLine($"[ResolvedCatalog] snapshot not usable (schema {snapshot.SchemaVersion}, generated {snapshot.GeneratedAt:u}); using the live API.");
                return snapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ResolvedCatalog] fetch failed: {ex.Message}");
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
                Trace.WriteLine($"[ResolvedCatalog] cache write failed: {ex.Message}");
            }
        }
    }
}
