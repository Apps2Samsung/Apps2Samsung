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
    private readonly Func<IReadOnlyList<UserSource>> _userSources;
    private readonly Apps2Samsung.Catalog.Repositories.SignedRepositories _repositories;
    private readonly Action<string>? _warn;

    /// <param name="userSources">The sources the user added (stored plus --source), read on every load.</param>
    /// <param name="warn">Where a signed repository's trouble is said (#1018): a changed key, a failed check.</param>
    public CatalogLoader(HttpClient http, string token, Func<IReadOnlyList<UserSource>>? userSources = null, Action<string>? warn = null)
    {
        _warn = warn;
        _repositories = new Apps2Samsung.Catalog.Repositories.SignedRepositories(http, Path.Combine(CliPaths.CliDir, "repositories"));
        _http = http;
        _token = token;
        _userSources = userSources ?? (() => Array.Empty<UserSource>());
        var snapshot = new ResolvedCatalogService(http, Path.Combine(CliPaths.CliDir, ResolvedCatalogService.CacheFileName));
        _releases = new AddLatestRelease(http, () => token, snapshot);
        _downloadStats = new DownloadStatsService(http, Path.Combine(CliPaths.CliDir, DownloadStatsService.CacheFileName));
    }

    /// <param name="Removed">Apps the official catalog stopped distributing (#1033): never in <paramref name="Entries"/>,
    /// only to flag an installed copy and to say why `install` will not find one.</param>
    public sealed record Catalog(ProviderManifest Manifest, IReadOnlyList<GitHubRelease> Entries, int FailedProviders, IReadOnlySet<string> MultiVersionApps,
        IReadOnlyList<CommunityCatalogRemoved>? Removed = null)
    {
        public IReadOnlyList<CommunityCatalogRemoved> RemovedApps => Removed ?? Array.Empty<CommunityCatalogRemoved>();
    }

    /// <param name="allVersions">Keep every release of an app with a version history (Jellyfin) instead of the latest.</param>
    public async Task<Catalog> LoadAsync(bool allVersions, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct);
        // The manifest's providers, then the sources the user added themselves (UserSources), feeds
        // read and unfolded, as the apps do.
        var providers = (await UserSources.ResolveAsync(_http, manifest.Providers, _userSources(), ct, _repositories, Report))
            .Where(p => !string.IsNullOrWhiteSpace(p.Url)).ToList();
        var multiVersion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new List<CommunityCatalogRemoved>();
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
                // Only the official catalog says what it removed; a user's own source cannot (#1033).
                if (!provider.IsUserSource && provider.Source is null or { Kind: CatalogSourceKind.Official })
                    lock (removed) removed.AddRange(CommunityAppList.Removed(catalog));
            }
            else
            {
                var category = AppCategories.Normalize(provider.Category);
                foreach (var r in fetched.Releases)
                {
                    r.RequiresPartner = requiresPartner;
                    r.Category = category;
                    r.IsUserSource = provider.IsUserSource;
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
        return new Catalog(manifest, list, results.Count(r => !r.Ok), multiVersion, removed);
    }

    /// <summary>A repository that did not load cleanly, said once per load (#1018).</summary>
    private void Report(Apps2Samsung.Catalog.Repositories.RepositoryLoad load)
    {
        var name = UserSources.DisplayName(load.Source);
        switch (load.State)
        {
            case Apps2Samsung.Catalog.Repositories.RepositoryState.KeyChanged:
                _warn?.Invoke($"{name}: the publisher's key changed (now {Apps2Samsung.Catalog.Repositories.RepositoryVerifier.FormatFingerprint(load.NewFingerprint!)}). Its apps are left out until you approve the new key: apps2samsung-cli sources add {load.Source.Url}");
                break;
            case Apps2Samsung.Catalog.Repositories.RepositoryState.Stale:
                _warn?.Invoke($"{name} could not be verified ({load.Message}); its apps are listed from the last verified copy and cannot be installed until it verifies again");
                break;
            case Apps2Samsung.Catalog.Repositories.RepositoryState.Failed:
                _warn?.Invoke($"{name} could not be verified ({load.Message}); its apps are missing from this list");
                break;
        }
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
        // One name: every entry under it, so a caller sees each source that offers it (#1018).
        return partial.Count == 1
            ? (partial[0], all.Where(e => string.Equals(e.Name, partial[0].Name, StringComparison.OrdinalIgnoreCase)).ToList())
            : (null, partial);
    }
}
