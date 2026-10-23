// Builds docs/catalog/resolved.json: the app catalog with every provider's GitHub releases already
// fetched, so the heads can list apps without a single api.github.com call (see
// Apps2Samsung.Core/Models/ResolvedCatalog.cs for the why).
//
//   dotnet run --project tools/CatalogSnapshot -- <third-party-apps.json> <out: resolved.json>
//
// GITHUB_TOKEN in the environment authenticates the API calls (the workflow's token: ~1000 req/h
// instead of 60). Without it the tool still works, for a local dry run.
//
// A provider the API fails on is written with ok:false and the releases carried over from the
// previous file at <out>, so one flaky upstream never removes an app from everyone's list; the
// heads skip ok:false entries and fetch those providers live themselves. Exit code 1 when nothing
// at all could be resolved (the file is then left untouched), so the workflow never publishes an
// empty catalog.

using System.Text.Json;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: CatalogSnapshot <third-party-apps.json> <resolved.json>");
    return 2;
}

var manifestPath = args[0];
var outputPath = args[1];

var manifest = JsonSerializer.Deserialize<ProviderManifest>(
    await File.ReadAllTextAsync(manifestPath), JsonSerializerOptionsProvider.Default);
if (manifest is null || manifest.Providers.Count == 0)
{
    Console.Error.WriteLine($"::error::{manifestPath} has no providers");
    return 1;
}

ResolvedCatalog? previous = null;
if (File.Exists(outputPath))
{
    previous = ResolvedCatalogService.Parse(await File.ReadAllTextAsync(outputPath));
    Console.WriteLine(previous is null
        ? $"Previous snapshot at {outputPath} did not parse; starting fresh."
        : $"Previous snapshot: generated {previous.GeneratedAt:u}, {previous.Providers.Count} providers.");
}

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
Console.WriteLine(string.IsNullOrWhiteSpace(token)
    ? "No GITHUB_TOKEN: resolving unauthenticated (60 req/h)."
    : "Resolving with GITHUB_TOKEN.");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
// Package downloads for the manifest read (ManifestInfo): tens of MB each, so a longer budget.
using var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var releases = new AddLatestRelease(http, () => token);
var buildInfo = new BuildInfoService(http, () => token);

var snapshot = new ResolvedCatalog
{
    GeneratedAt = DateTimeOffset.UtcNow,
    ManifestSchemaVersion = manifest.SchemaVersion,
};

var okCount = 0;
foreach (var provider in manifest.Providers)
{
    if (string.IsNullOrWhiteSpace(provider.Url))
        continue;

    var entry = new ResolvedProvider { Id = provider.Id ?? string.Empty, Url = provider.Url };
    snapshot.Providers.Add(entry);

    // The full take: the heads trim to 1 themselves unless the user opted into the Jellyfin history.
    var (list, ok) = await releases.GetReleasesWithStatusAsync(
        provider.Url, provider.Prefix, provider.DisplayName, provider.Take);

    // Drafts sort to the top of an authenticated /releases listing and their assets 404; a head
    // reading the snapshot cannot know, so never let one through. (Only ever visible for repos the
    // token can write, i.e. this organisation's own.)
    list = list.Where(r => !r.Draft).ToList();

    var tag = ok ? await buildInfo.FetchLatestReleaseTagAsync(provider.Url) : string.Empty;
    if (ok && string.IsNullOrEmpty(tag) && list.Count > 0)
        tag = list[0].TagName;

    var old = previous?.Providers.FirstOrDefault(p =>
        (!string.IsNullOrWhiteSpace(entry.Id) && string.Equals(p.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
        || string.Equals(p.Url, entry.Url, StringComparison.OrdinalIgnoreCase));

    if (!ok)
    {
        entry.Ok = false;
        entry.LatestTag = old?.LatestTag ?? string.Empty;
        entry.Releases = old?.Releases ?? new List<GitHubRelease>();
        entry.Catalog = old?.Catalog;
        Console.WriteLine($"::warning::{provider.Id} ({provider.Url}): fetch failed; carried over {entry.Releases.Count} release(s) from the previous snapshot as ok:false");
        continue;
    }

    entry.Ok = true;
    entry.LatestTag = tag;
    entry.Releases = list;
    // The community bundle's catalog.json lives on github.com/.../releases/download (not rate
    // limited), but inlining it saves the heads that request as well.
    if (provider.ExpandAssets && list.Count > 0)
        entry.Catalog = await releases.GetCommunityCatalogAsync(list[0].CatalogUrl);
    // Everyone else: the version and ids from inside each asset, so the heads can compare an
    // installed Jellyfin or TizenTube against the catalog the same way as a community app (#820).
    // Unchanged assets keep last snapshot's values; only new releases are downloaded.
    else
        await ManifestInfo.FillAsync(downloads, entry.Id, list, old);

    okCount++;
    var assets = list.Sum(r => r.Assets.Count);
    Console.WriteLine($"{provider.Id,-28} {tag,-18} {list.Count} release(s), {assets} asset(s){(entry.Catalog is null ? "" : $", catalog with {entry.Catalog.Apps.Count} apps")}");
}

if (okCount == 0)
{
    Console.Error.WriteLine("::error::No provider could be resolved; leaving the existing snapshot untouched.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
var json = JsonSerializer.Serialize(snapshot, JsonSerializerOptionsProvider.Indented);
await File.WriteAllTextAsync(outputPath, json + "\n");
Console.WriteLine($"Wrote {outputPath}: {okCount}/{snapshot.Providers.Count} providers ok, {json.Length:N0} bytes.");
return 0;
