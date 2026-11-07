using Apps2Samsung.Catalog;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Core
{
    public class AddLatestRelease
    {
        private readonly HttpClient _httpClient;
        private readonly Func<string?>? _tokenProvider;
        private readonly ResolvedCatalogService? _snapshot;

        /// <param name="tokenProvider">
        /// Optional GitHub PAT source. The desktop leaves this null (its HttpClient handler injects
        /// the token); the mobile head passes its settings token so API calls dodge rate limits.
        /// </param>
        /// <param name="snapshot">
        /// Optional pre-resolved catalog (apps2samsung.com). When set, <see cref="GetProviderReleasesAsync"/>
        /// serves a manifest provider from it and only falls back to the GitHub API when the snapshot
        /// has nothing usable for that provider.
        /// </param>
        public AddLatestRelease(HttpClient httpClient, Func<string?>? tokenProvider = null, ResolvedCatalogService? snapshot = null)
        {
            _httpClient = httpClient;
            _tokenProvider = tokenProvider;
            _snapshot = snapshot;
        }

        /// <summary>
        /// One manifest provider's releases, plus where they came from. <paramref name="Catalog"/>
        /// is the community bundle's <c>catalog.json</c> when the snapshot carried it inline, so the
        /// caller can skip that download too; null means "fetch it yourself if you need it".
        /// </summary>
        public sealed record ProviderReleases(List<GitHubRelease> Releases, bool Ok, CommunityCatalog? Catalog, ProviderReleaseSource Source);

        public enum ProviderReleaseSource { Snapshot, Live, CachedSnapshot, None, Repository }

        /// <summary>
        /// Releases for a manifest provider, without an API call when it can be helped:
        /// 1. the pre-resolved snapshot, when it has an <c>ok</c> entry for this provider;
        /// 2. else the live GitHub API (<see cref="GetReleasesWithStatusAsync"/>, with the PAT if any);
        /// 3. else the last snapshot this machine cached, however old: stale beats empty.
        /// <c>Ok</c> keeps its meaning (false only when every source failed), so the heads' "N providers
        /// failed" counter still works. A provider that is not in the snapshot (a user-added one) takes
        /// the live path exactly as before.
        /// </summary>
        public async Task<ProviderReleases> GetProviderReleasesAsync(ProviderEntry provider, int take)
        {
            var result = await GetProviderReleasesCoreAsync(provider, take);
            // Provenance (#1018): the official manifest's own providers keep the default.
            var source = provider.Source ?? (provider.IsUserSource ? new CatalogSource(CatalogSourceKind.Unverified, provider.DisplayName) : null);
            if (source is not null)
                foreach (var release in result.Releases)
                    release.Source = source;
            return result;
        }

        private async Task<ProviderReleases> GetProviderReleasesCoreAsync(ProviderEntry provider, int take)
        {
            if (take < 1) take = 1;

            // A signed repository's releases came with its verified manifest (#1018): no API call.
            if (provider.Releases is not null)
                return new ProviderReleases(
                    ResolvedCatalogService.CopyReleases(new ResolvedProvider { Releases = provider.Releases }, take), true, null, ProviderReleaseSource.Repository);

            if (_snapshot is not null)
            {
                var snapshot = await _snapshot.GetAsync();
                var resolved = ResolvedCatalogService.Find(snapshot, provider);
                if (resolved is not null)
                    return new ProviderReleases(
                        ResolvedCatalogService.CopyReleases(resolved, take), true, resolved.Catalog, ProviderReleaseSource.Snapshot);
            }

            var (releases, ok) = await GetReleasesWithStatusAsync(provider.Url, provider.Prefix, provider.DisplayName, take);
            if (ok)
                return new ProviderReleases(releases, true, null, ProviderReleaseSource.Live);

            if (_snapshot is not null)
            {
                var cached = ResolvedCatalogService.Find(await _snapshot.GetCachedAsync(), provider);
                if (cached is not null)
                {
                    Trace.WriteLine($"[AddLatestRelease] {provider.Url}: live fetch failed, serving the cached snapshot from {cached.LatestTag}.");
                    return new ProviderReleases(
                        ResolvedCatalogService.CopyReleases(cached, take), true, cached.Catalog, ProviderReleaseSource.CachedSnapshot);
                }
            }

            return new ProviderReleases(releases, false, null, ProviderReleaseSource.None);
        }

        // GitHub rejects UA-less requests; attach a User-Agent (and the PAT for GitHub hosts).
        private HttpRequestMessage BuildRequest(string url)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Apps2Samsung");

            var token = _tokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(token) &&
                new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return request;
        }

        /// <summary>Name of the per-file index the community bundle release carries (see <see cref="CommunityCatalog"/>).</summary>
        public const string CatalogFileName = "catalog.json";

        /// <summary>
        /// Downloads and parses a release's <c>catalog.json</c>. Null when there is none, when the
        /// download fails or when it does not parse: the caller then lists the files uncategorised,
        /// exactly as before the catalog existed.
        /// </summary>
        public async Task<CommunityCatalog?> GetCommunityCatalogAsync(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            try
            {
                using var request = BuildRequest(url);
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var catalog = JsonSerializer.Deserialize<CommunityCatalog>(json, JsonSerializerOptionsProvider.Default);
                return catalog is { Apps.Count: > 0 } ? catalog : null;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to fetch community catalog from {url}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Fetches releases, discarding failure status (empty list on any error).</summary>
        public async Task<List<GitHubRelease>> GetReleasesAsync(string url, string prefix, string displayName, int take = 1)
            => (await GetReleasesWithStatusAsync(url, prefix, displayName, take)).Releases;

        /// <summary>
        /// Fetches releases and reports whether the HTTP call succeeded. Handles both the /releases
        /// (list) and /releases/tags/&lt;tag&gt; (single) shapes, keeps only .wgt/.tpk assets,
        /// truncates to <paramref name="take"/>, and applies the display name/prefix.
        /// <c>Ok=false</c> means the request failed (network / GitHub rate limit), which the caller
        /// can distinguish from a successful fetch that simply had no installable assets.
        /// </summary>
        public async Task<(List<GitHubRelease> Releases, bool Ok)> GetReleasesWithStatusAsync(
            string url, string prefix, string displayName, int take = 1)
        {
            if (take < 1) take = 1;

            // A user source that is a link to the file itself: no release list to read, the file is
            // the release (see GetDirectFileAsync).
            if (UserSources.IsDirectFile(url))
                return await GetDirectFileAsync(url, displayName);

            try
            {
                using var request = BuildRequest(url);
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                List<GitHubRelease> releases;
                try
                {
                    releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonSerializerOptionsProvider.Default)
                        ?? new List<GitHubRelease>();
                }
                catch (JsonException)
                {
                    var single = JsonSerializer.Deserialize<GitHubRelease>(json, JsonSerializerOptionsProvider.Default);
                    releases = single is null ? new List<GitHubRelease>() : new List<GitHubRelease> { single };
                }

                foreach (var r in releases)
                {
                    // The community bundle ships a catalog.json next to its packages; remember where
                    // it is before the asset list is narrowed down to installable files.
                    r.CatalogUrl = r.Assets?
                        .FirstOrDefault(a => string.Equals(a.FileName, CatalogFileName, StringComparison.OrdinalIgnoreCase))
                        ?.DownloadUrl;
                    r.Assets = r.Assets?
                        .Where(a => !string.IsNullOrWhiteSpace(a.FileName) &&
                            (a.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) ||
                             a.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase)))
                        .ToList() ?? new List<Asset>();
                }

                releases = releases.Where(r => r.Assets.Count > 0).ToList();
                if (releases.Count == 0)
                    return (new List<GitHubRelease>(), true); // reached GitHub fine, just nothing installable

                var result = releases.Count > take ? releases.GetRange(0, take) : releases;
                foreach (var r in result)
                    r.Name = string.IsNullOrWhiteSpace(displayName) ? $"{prefix}{r.Name}" : displayName;

                return (result, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to fetch release from {url}: {ex}");
                return (new List<GitHubRelease>(), false);
            }
        }

        /// <summary>
        /// A direct .wgt/.tpk link as one release with one asset: the file's name, its size and its
        /// Last-Modified date as the version, read from the headers (HEAD, or a GET whose body is
        /// not downloaded when the host refuses HEAD). A developer's nightly at a fixed URL lists as
        /// "updated on" that date, and Refresh re-reads the headers. <c>Ok=false</c> when the host
        /// could not be reached or refused the request.
        /// </summary>
        private async Task<(List<GitHubRelease> Releases, bool Ok)> GetDirectFileAsync(string url, string displayName)
        {
            try
            {
                using var response = await SendHeadOrGetAsync(url);
                response.EnsureSuccessStatusCode();

                var fileName = UserSources.FileName(url);
                var modified = response.Content.Headers.LastModified;
                var release = new GitHubRelease
                {
                    Name = string.IsNullOrWhiteSpace(displayName) ? System.IO.Path.GetFileNameWithoutExtension(fileName) : displayName,
                    TagName = modified?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    PublishedAt = modified?.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Url = url,
                    Assets = new List<Asset>
                    {
                        new()
                        {
                            FileName = fileName,
                            DownloadUrl = url,
                            Size = response.Content.Headers.ContentLength ?? 0,
                        },
                    },
                };
                return (new List<GitHubRelease> { release }, true);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to read the file at {url}: {ex.Message}");
                return (new List<GitHubRelease>(), false);
            }
        }

        private async Task<HttpResponseMessage> SendHeadOrGetAsync(string url)
        {
            using (var head = BuildRequest(url))
            {
                head.Method = HttpMethod.Head;
                var response = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                // Some hosts (and some CDNs in front of a release asset) answer HEAD with 403/405; the
                // headers of a GET say the same, and the body is left unread.
                if (response.IsSuccessStatusCode
                    || response.StatusCode is not (System.Net.HttpStatusCode.MethodNotAllowed
                        or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotImplemented))
                    return response;
                response.Dispose();
            }

            using var get = BuildRequest(url);
            return await _httpClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
        }
    }
}