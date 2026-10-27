// Fills each release asset's manifest version and ids (Asset.PackageVersion / AppId / PackageId)
// for the providers whose packages are plain GitHub release assets: the Jellyfin family, TizenTube
// and the like. The community bundle is skipped here; its catalog.json already carries the same
// three fields per file (tizen-community-packages#133), and CommunityAppList stamps them.
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

using System.Diagnostics;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

internal static class ManifestInfo
{
    public static async Task FillAsync(HttpClient downloads, string providerId, List<GitHubRelease> releases, ResolvedProvider? previous)
    {
        var known = (previous?.Releases ?? new List<GitHubRelease>())
            .SelectMany(r => r.Assets)
            .Where(a => !string.IsNullOrWhiteSpace(a.DownloadUrl) &&
                        (a.PackageVersion is not null || a.AppId is not null || a.PackageId is not null))
            .GroupBy(a => a.DownloadUrl, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int downloaded = 0, reused = 0, failed = 0, skipped = 0;
        var sw = Stopwatch.StartNew();
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

                if (known.TryGetValue(asset.DownloadUrl, out var old) && old.Size == asset.Size &&
                    (asset.Digest is not null || old.Digest is not null))
                {
                    asset.PackageVersion = old.PackageVersion;
                    asset.AppId = old.AppId;
                    asset.PackageId = old.PackageId;
                    asset.Digest ??= old.Digest;
                    reused++;
                    continue;
                }

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
                    downloaded++;
                    if (info.Version is null && info.ApplicationId is null)
                        Console.WriteLine($"::warning::{providerId}: {name} has no readable manifest; no version recorded");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"::warning::{providerId}: could not read {name} ({ex.GetType().Name}: {ex.Message}); no version recorded");
                }
                finally
                {
                    try { File.Delete(tmp); } catch { /* best effort */ }
                }
            }
        }

        Console.WriteLine($"{providerId,-28} manifests: {downloaded} downloaded, {reused} carried over, {failed} failed, {skipped} skipped ({sw.Elapsed.TotalSeconds:0}s)");
    }
}
