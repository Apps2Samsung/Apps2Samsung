using System;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The 16:9 preview image for a catalog entry, from the provider manifest: a community app's
    /// own image (matched by name), a provider's build-info image (Moonfin, Litefin, …), or the
    /// Jellyfin default for anything Jellyfin. Null when the manifest has nothing for it.
    /// </summary>
    public static class CatalogPreviews
    {
        public static string? For(ProviderManifest? manifest, GitHubRelease release)
        {
            if (manifest is null)
                return null;
            var name = release.Name ?? string.Empty;

            var community = manifest.CommunityApps
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.MatchName) && !string.IsNullOrWhiteSpace(c.PreviewImage)
                                     && name.Contains(c.MatchName, StringComparison.OrdinalIgnoreCase));
            if (community is not null)
                return community.PreviewImage;

            var provider = manifest.Providers
                .FirstOrDefault(p => p.BuildInfo is { } bi && !string.IsNullOrWhiteSpace(bi.Name) && !string.IsNullOrWhiteSpace(bi.PreviewImage)
                                     && name.Contains(bi.Name, StringComparison.OrdinalIgnoreCase));
            if (provider?.BuildInfo?.PreviewImage is { } image)
                return image;

            if (name.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(manifest.PreviewImages?.Jellyfin))
                return manifest.PreviewImages.Jellyfin;

            return null;
        }
    }
}
