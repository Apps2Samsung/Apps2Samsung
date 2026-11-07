// Fills each release asset's manifest version, ids, the Tizen version it asks for and its icon
// (Asset.PackageVersion / AppId / PackageId / RequiredVersion / Icon + IconDigest) for every provider
// whose packages are GitHub release assets: the Jellyfin family, TizenTube and the like, and the
// community bundle. For the bundle its catalog.json already carries the manifest fields and
// CommunityAppList stamps them on top; it is read here all the same since the icon only comes out
// of the package itself (#1000).
//
// The TV's app list reports the version from a package's own manifest, so recording that same
// manifest version in the snapshot is what lets the heads compare installed against catalog for
// these apps too (#820). An asset whose download URL and size are unchanged since the previous
// snapshot keeps the fields it had, so a 30-minute cron tick downloads nothing; only a genuinely
// new release costs its assets' bytes once.
//
// The same download also fills Asset.Digest (sha256:<hex>) when GitHub's API has none for the
// asset (releases from before GitHub started recording digests): TV-Store only installs a package
// whose hash is in the signed catalog (#881).
//
// And it takes the icon out of the package (WgtManifest.ReadIconAsync) and publishes it through
// IconStore, named by its SHA-256, with the URL and hash on the asset: TV-Store shows an icon only
// when its bytes hash to icon_digest (TV-Store#66), so the two always come from one file in one run.

using System.Diagnostics;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

internal static class ManifestInfo
{
    // TV-Store shows no icon above this (its icons.js MAX_BYTES), so none is published either.
    private const int MaxIconBytes = 1024 * 1024;
    // Package downloads in flight at once; GitHub's release CDN takes this without complaint.
    private const int Parallelism = 8;

    /// <param name="rereadMissingIcons">Read every asset without an icon again, as the first icon
    /// pass did: for a run after the icon reader learned a new place to look (#1000).</param>
    public static async Task FillAsync(HttpClient downloads, string providerId, List<GitHubRelease> releases, ResolvedProvider? previous, IconStore icons, bool rereadMissingIcons = false)
    {
        var known = (previous?.Releases ?? new List<GitHubRelease>())
            .SelectMany(r => r.Assets)
            .Where(a => !string.IsNullOrWhiteSpace(a.DownloadUrl) &&
                        (a.PackageVersion is not null || a.AppId is not null || a.PackageId is not null || a.RequiredVersion is not null || a.Icon is not null))
            .GroupBy(a => a.DownloadUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // The first run after icons were added (#1000) has to read every asset once more. An asset
        // without an icon in the previous snapshot is read again until this provider has one icon
        // in there: from then on an asset without one has been looked at and simply ships none.
        // (Per provider rather than for the whole snapshot, so a provider that was carried over as
        // ok:false during that first run still gets its icons when it is back.)
        var iconPassDone = !rereadMissingIcons && known.Values.Any(a => a.Icon is not null);

        int downloaded = 0, reused = 0, failed = 0, skipped = 0, withIcon = 0;
        var sw = Stopwatch.StartNew();
        var toRead = new List<(GitHubRelease Release, Asset Asset)>();
        foreach (var release in releases)
        {
            foreach (var asset in release.Assets)
            {
                var name = asset.FileName ?? string.Empty;
                if (!name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                // An icon whose file is gone from the folder is read again too, so the published
                // URL is never a dead link.
                if (known.TryGetValue(asset.DownloadUrl, out var old) && old.Size == asset.Size &&
                    (asset.Digest is not null || old.Digest is not null) &&
                    (old.Icon is not null ? icons.Has(old.Icon) : iconPassDone))
                {
                    asset.PackageVersion = old.PackageVersion;
                    asset.AppId = old.AppId;
                    asset.PackageId = old.PackageId;
                    asset.RequiredVersion = old.RequiredVersion;
                    asset.Icon = old.Icon;
                    asset.IconDigest = old.IconDigest;
                    asset.Digest ??= old.Digest;
                    reused++;
                    if (asset.Icon is not null)
                        withIcon++;
                    continue;
                }

                toRead.Add((release, asset));
            }
        }

        // A package takes a second or two to fetch, and the first pass after a new field (icons,
        // #1000) reads some 400 of them: one after another that is longer than the job's budget,
        // eight at a time it is a few minutes. Each asset is touched by one task only; the counters
        // are shared.
        await Parallel.ForEachAsync(toRead, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, async (item, _) =>
        {
            var asset = item.Asset;
            var name = asset.FileName ?? string.Empty;
            var tmp = Path.Combine(Path.GetTempPath(), $"a2s-snapshot-{Guid.NewGuid():N}{Path.GetExtension(name)}");
            try
            {
                using (var response = await downloads.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(tmp);
                    await response.Content.CopyToAsync(file);
                }

                if (asset.Digest is null)
                {
                    await using var read = File.OpenRead(tmp);
                    asset.Digest = "sha256:" + Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read));
                }

                var info = await WgtManifest.ReadPackageInfoAsync(tmp);
                asset.PackageVersion = info.Version;
                asset.AppId = info.ApplicationId;
                asset.PackageId = info.PackageId;
                asset.RequiredVersion = info.RequiredVersion;
                Interlocked.Increment(ref downloaded);
                if (info.Version is null && info.ApplicationId is null)
                    Console.WriteLine($"::warning::{providerId}: {name} has no readable manifest; no version recorded");

                var icon = await WgtManifest.ReadIconAsync(tmp);
                if (icon is null)
                    Console.WriteLine($"{providerId}: {name} has no readable PNG/JPEG/GIF/WebP icon; none published");
                else if (icon.Bytes.Length > MaxIconBytes)
                    Console.WriteLine($"::warning::{providerId}: {name}'s icon is {icon.Bytes.Length:N0} bytes, more than TV-Store shows ({MaxIconBytes:N0}); none published");
                else
                {
                    (asset.Icon, asset.IconDigest) = icons.Store(icon);
                    Interlocked.Increment(ref withIcon);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.WriteLine($"::warning::{providerId}: could not read {name} ({ex.GetType().Name}: {ex.Message}); no version recorded");
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        });

        Console.WriteLine($"{providerId,-28} manifests: {downloaded} downloaded, {reused} carried over, {failed} failed, {skipped} skipped, {withIcon} with an icon ({sw.Elapsed.TotalSeconds:0}s)");
    }
}
