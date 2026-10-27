using System.Net.Http.Headers;
using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

namespace Apps2Samsung.Cli;

/// <summary>
/// The app catalog as the heads build it: the provider manifest from the main branch (cached for
/// a run without network), each provider's releases through Core's <see cref="AddLatestRelease"/>
/// (the pre-resolved snapshot first, the GitHub API when it lacks one), and the community bundle
/// expanded by <see cref="CommunityAppList"/>. Same shaping as the phone's CatalogService.
/// </summary>
public sealed class CatalogLoader
{
    private const string ManifestUrl = "https://raw.githubusercontent.com/Apps2Samsung/Apps2Samsung/main/third-party-apps.json";

    private readonly HttpClient _http;
    private readonly AddLatestRelease _releases;
    private readonly DownloadStatsService _downloadStats;
    private readonly string _token;

    public CatalogLoader(HttpClient http, string token)
    {
        _http = http;
        _token = token;
        var snapshot = new ResolvedCatalogService(http, Path.Combine(CliPaths.CliDir, ResolvedCatalogService.CacheFileName));
        _releases = new AddLatestRelease(http, () => token, snapshot);
        _downloadStats = new DownloadStatsService(http, Path.Combine(CliPaths.CliDir, DownloadStatsService.CacheFileName));
    }

    public sealed record Catalog(ProviderManifest Manifest, IReadOnlyList<GitHubRelease> Entries, int FailedProviders, IReadOnlySet<string> MultiVersionApps);

    /// <param name="allVersions">Keep every release of an app with a version history (Jellyfin) instead of the latest.</param>
    public async Task<Catalog> LoadAsync(bool allVersions, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct);
        var providers = manifest.Providers.Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
        var multiVersion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Fetched alongside the providers; never fails, at worst there are no counts.
        var stats = _downloadStats.GetAsync(ct);

        var tasks = providers.Select(async provider =>
        {
            var take = provider.Take > 1 && !allVersions ? 1 : provider.Take;
            var fetched = await _releases.GetProviderReleasesAsync(provider, take);
            var requiresPartner = string.Equals(provider.CertLevel, "partner", StringComparison.OrdinalIgnoreCase);

            var entries = new List<GitHubRelease>();
            if (provider.ExpandAssets)
            {
                var catalog = fetched.Catalog ?? (fetched.Releases.Count > 0
                    ? await _releases.GetCommunityCatalogAsync(fetched.Releases[0].CatalogUrl)
                    : null);
                entries.AddRange(CommunityAppList.Expand(fetched.Releases, catalog, requiresPartner));
            }
            else
            {
                var category = AppCategories.Normalize(provider.Category);
                foreach (var r in fetched.Releases)
                {
                    r.RequiresPartner = requiresPartner;
                    r.Category = category;
                    SourceRepo.StampFromApiUrl(r, provider.Url);
                    r.Description = provider.BuildInfo?.Description?.Trim() ?? string.Empty;
                    if (provider.Take > 1)
                        lock (multiVersion) multiVersion.Add(r.Name);
                }
                entries.AddRange(fetched.Releases);
            }
            DownloadStatsService.Stamp(entries, provider, await stats);
            return (Entries: entries, fetched.Ok);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var list = results.SelectMany(r => r.Entries).ToList();
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new Catalog(manifest, list, results.Count(r => !r.Ok), multiVersion);
    }

    // Remote, else the last copy that worked.
    private async Task<ProviderManifest> GetManifestAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(CliPaths.CliDir);
        var cachePath = Path.Combine(CliPaths.CliDir, "third-party-apps.cache.json");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            req.Headers.UserAgent.ParseAdd("Apps2Samsung-CLI");
            if (!string.IsNullOrWhiteSpace(_token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var manifest = JsonSerializer.Deserialize<ProviderManifest>(json, JsonSerializerOptionsProvider.Default);
            if (manifest is { Providers.Count: > 0 })
            {
                try { File.WriteAllText(cachePath, json); } catch { /* cache is best-effort */ }
                return manifest;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to the cache.
        }

        if (File.Exists(cachePath))
        {
            var manifest = JsonSerializer.Deserialize<ProviderManifest>(File.ReadAllText(cachePath), JsonSerializerOptionsProvider.Default);
            if (manifest is { Providers.Count: > 0 })
                return manifest;
        }
        throw new InvalidOperationException("The app catalog could not be loaded: no network, and no cached copy yet.");
    }

    /// <summary>
    /// The catalog entry for <paramref name="query"/>: an exact name match (case-insensitive) wins;
    /// else a single entry whose name contains the words; else null with the candidates.
    /// </summary>
    public static (GitHubRelease? Match, IReadOnlyList<GitHubRelease> Candidates) Find(IEnumerable<GitHubRelease> entries, string query)
    {
        var all = entries.ToList();
        var exact = all.Where(e => string.Equals(e.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return (exact[0], exact);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var partial = all
            .Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return partial.Count == 1 ? (partial[0], partial) : (null, partial);
    }
}
