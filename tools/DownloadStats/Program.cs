// Builds docs/catalog/downloads.json: every manifest provider's all-time download count, summed over
// all of its GitHub releases (see Apps2Samsung.Core/Models/DownloadStats.cs for the why).
//
//   dotnet run --project tools/DownloadStats -- <third-party-apps.json> <out: downloads.json> <history.json>
//
// GITHUB_TOKEN in the environment authenticates the API calls; the Jellyfin builds repo alone is
// some 2,000 releases (20+ pages). Without it the tool still works, for a local dry run.
//
// <history.json> keeps one day's totals per provider and file for the last RecentDays days; the
// "recent" counts are today's totals minus the oldest day in it. A total never goes down: when a
// release is deleted upstream its downloads would vanish from the sum, so the previous total is
// kept instead. Exit code 1 when no provider could be read (the files are then left untouched).
//
// The community bundle counts per file, and only the files its latest release ships: an app that
// was dropped or renamed is gone from the list, and so are its old counts. A file counts every
// earlier release of the same name, version aside (Foo-v1.wgt and Foo-v2.wgt are one app). Any
// other provider lists as one app, so its total is every asset of every release.

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

const int RecentDays = 30;
// community-1 to community-409 were made while the sync workflow was being built (Nov 2025) and
// carry next to no downloads; several were deleted and their counts are gone. Real use starts at
// community-410 (2025-11-27), so that is where the community counts start.
const int FirstCountedCommunityRelease = 410;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: DownloadStats <third-party-apps.json> <downloads.json> <history.json>");
    return 2;
}

var (manifestPath, outputPath, historyPath) = (args[0], args[1], args[2]);

var manifest = JsonSerializer.Deserialize<ProviderManifest>(
    await File.ReadAllTextAsync(manifestPath), JsonSerializerOptionsProvider.Default);
if (manifest is null || manifest.Providers.Count == 0)
{
    Console.Error.WriteLine($"::error::{manifestPath} has no providers");
    return 1;
}

var previous = File.Exists(outputPath)
    ? JsonSerializer.Deserialize<DownloadStats>(await File.ReadAllTextAsync(outputPath), JsonSerializerOptionsProvider.Default)
    : null;
var history = File.Exists(historyPath)
    ? JsonSerializer.Deserialize<History>(await File.ReadAllTextAsync(historyPath), JsonSerializerOptionsProvider.Default) ?? new History()
    : new History();

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
http.DefaultRequestHeaders.UserAgent.ParseAdd("Apps2Samsung-DownloadStats");
if (!string.IsNullOrWhiteSpace(token))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

// A provider pinned to one tag (jellyfin-legacy) shares its repo with a provider that lists all of
// them; that tag is counted for the pinned provider only.
var providers = manifest.Providers
    .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Url))
    .Select(p => (Entry: p, Source: ReleaseSource.Parse(p.Url)))
    .Where(p => p.Source is not null)
    .Select(p => (p.Entry, Source: p.Source!))
    .ToList();
var pinnedTags = providers.Where(p => p.Source.Tag is not null)
    .Select(p => $"{p.Source.Repo}@{p.Source.Tag}")
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

var repoReleases = new Dictionary<string, List<Release>>(StringComparer.OrdinalIgnoreCase);
var stats = new DownloadStats { GeneratedAt = DateTimeOffset.UtcNow };
var today = new HistoryDay { Date = DateOnly.FromDateTime(stats.GeneratedAt.UtcDateTime).ToString("yyyy-MM-dd") };
var okCount = 0;

foreach (var (provider, source) in providers)
{
    List<Release> releases;
    try
    {
        if (source.Tag is not null)
            releases = [await FetchReleaseAsync($"https://api.github.com/repos/{source.Repo}/releases/tags/{source.Tag}")];
        else
        {
            if (!repoReleases.TryGetValue(source.Repo, out var all))
                repoReleases[source.Repo] = all = await FetchAllReleasesAsync(source.Repo);
            releases = all.Where(r => !pinnedTags.Contains($"{source.Repo}@{r.Tag}")).ToList();
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        // Keep yesterday's numbers rather than dropping the app from the ranking.
        if (previous?.Providers.TryGetValue(provider.Id, out var kept) == true)
        {
            stats.Providers[provider.Id] = kept;
            if (history.Days.LastOrDefault()?.Providers.TryGetValue(provider.Id, out var keptDay) == true)
                today.Providers[provider.Id] = keptDay;
        }
        Console.WriteLine($"::warning::{provider.Id} ({source.Repo}): {ex.Message}; kept the previous counts");
        continue;
    }

    var old = previous?.Providers.GetValueOrDefault(provider.Id);
    var day = new HistoryProvider();
    if (provider.ExpandAssets)
    {
        releases = releases.Where(r => CommunityNumber(r.Tag) >= FirstCountedCommunityRelease).ToList();
        var latest = releases.MaxBy(r => CommunityNumber(r.Tag));
        var current = latest?.Assets.Select(a => a.Name).Where(IsPackage).Distinct().ToList() ?? [];
        // Two files of today's bundle that differ only by version stay two files, matched exactly.
        var unique = current.GroupBy(VersionFree).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var counts = releases.SelectMany(r => r.Assets).Where(a => IsPackage(a.Name))
            .Select(a => (File: current.Contains(a.Name) ? a.Name : unique.GetValueOrDefault(VersionFree(a.Name)), a.DownloadCount))
            .Where(a => a.File is not null)
            .GroupBy(a => a.File!)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.DownloadCount));
        day.Files = current.ToDictionary(f => f, f => Math.Max(counts.GetValueOrDefault(f), old?.Files?.GetValueOrDefault(f)?.Total ?? 0));
        day.Total = day.Files.Values.Sum();
    }
    else
        day.Total = Math.Max(releases.SelectMany(r => r.Assets).Where(a => IsPackage(a.Name)).Sum(a => a.DownloadCount), old?.Total ?? 0);
    today.Providers[provider.Id] = day;
    okCount++;
    Console.WriteLine($"{provider.Id,-28} {releases.Count,5} release(s) {day.Total,10:N0} downloads{(day.Files is null ? "" : $" over {day.Files.Count} files")}");
}

if (okCount == 0)
{
    Console.Error.WriteLine("::error::No provider could be read; leaving the existing files untouched.");
    return 1;
}

// Today replaces an earlier run of the same day; anything older than the window drops off.
history.Days.RemoveAll(d => d.Date == today.Date);
history.Days.Add(today);
var cutoff = DateOnly.FromDateTime(stats.GeneratedAt.UtcDateTime).AddDays(-RecentDays).ToString("yyyy-MM-dd");
history.Days.RemoveAll(d => string.CompareOrdinal(d.Date, cutoff) < 0);
history.Days.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
var baseline = history.Days[0];
stats.RecentSince = baseline.Date;

foreach (var (id, day) in today.Providers)
{
    var then = baseline.Providers.GetValueOrDefault(id);
    stats.Providers[id] = new ProviderDownloads
    {
        Total = day.Total,
        Recent = Math.Max(0, day.Total - (then?.Total ?? day.Total)),
        Files = day.Files?.ToDictionary(f => f.Key, f => new FileDownloads
        {
            Total = f.Value,
            // A file the baseline day lacks is an app added since: all of its downloads are recent.
            Recent = Math.Max(0, f.Value - (then is null ? f.Value : then.Files?.GetValueOrDefault(f.Key) ?? 0)),
        }),
    };
}

foreach (var path in new[] { outputPath, historyPath })
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(stats, JsonSerializerOptionsProvider.Indented) + "\n");
await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(history, JsonSerializerOptionsProvider.Default) + "\n");
Console.WriteLine($"Wrote {outputPath}: {okCount}/{providers.Count} providers read, recent since {stats.RecentSince}; {history.Days.Count} day(s) of history.");
return 0;

async Task<List<Release>> FetchAllReleasesAsync(string repo)
{
    var all = new List<Release>();
    for (var page = 1; ; page++)
    {
        var json = await GetAsync($"https://api.github.com/repos/{repo}/releases?per_page=100&page={page}");
        var batch = JsonSerializer.Deserialize<List<Release>>(json, JsonSerializerOptionsProvider.GitHub) ?? [];
        all.AddRange(batch.Where(r => !r.Draft));
        if (batch.Count < 100)
            return all;
    }
}

async Task<Release> FetchReleaseAsync(string url)
    => JsonSerializer.Deserialize<Release>(await GetAsync(url), JsonSerializerOptionsProvider.GitHub)
       ?? throw new JsonException($"{url} returned no release");

async Task<string> GetAsync(string url)
{
    using var response = await http.GetAsync(url);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}

static bool IsPackage(string name)
    => name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase);

// A file name without its version: Foo-v1.2.wgt, Foo_v3.wgt and Foo-1.2.3.wgt are all Foo.wgt. Only a
// v-number or a three-part version: Overscan-tizen4 and tizen-youtube-tizen-5.0 are per-Tizen
// variants, not versions.
static string VersionFree(string name)
    => Regex.Replace(name, @"[-_ .](?:v\d+(?:\.\d+)*|\d+\.\d+\.\d+)(?=[-_ .]|$)", "", RegexOptions.IgnoreCase);

static int CommunityNumber(string tag)
{
    var match = Regex.Match(tag, @"^community-(\d+)");
    return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : -1;
}

// owner/repo and the tag, from a manifest URL: .../repos/{owner}/{repo}/releases[/tags/{tag}].
internal sealed record ReleaseSource(string Repo, string? Tag)
{
    public static ReleaseSource? Parse(string url)
    {
        var match = Regex.Match(url, @"/repos/([^/]+/[^/]+)/releases(?:/tags/([^/?#]+))?", RegexOptions.IgnoreCase);
        return match.Success
            ? new ReleaseSource(match.Groups[1].Value, match.Groups[2].Success ? Uri.UnescapeDataString(match.Groups[2].Value) : null)
            : null;
    }
}

internal sealed class Release
{
    [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
    public string Tag { get; set; } = "";
    public bool Draft { get; set; }
    public List<ReleaseAsset> Assets { get; set; } = [];
}

internal sealed class ReleaseAsset
{
    public string Name { get; set; } = "";
    public long DownloadCount { get; set; }
}

internal sealed class History
{
    public List<HistoryDay> Days { get; set; } = [];
}

internal sealed class HistoryDay
{
    public string Date { get; set; } = "";
    public Dictionary<string, HistoryProvider> Providers { get; set; } = new();
}

internal sealed class HistoryProvider
{
    public long Total { get; set; }
    public Dictionary<string, long>? Files { get; set; }
}
