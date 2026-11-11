using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The 16:9 preview image for a catalog entry. A community package carries its own in the
    /// catalog (<c>preview_image</c>, stamped on <see cref="GitHubRelease.PreviewImage"/>); otherwise
    /// it comes from the provider manifest: a community app's image matched by name (the older
    /// <c>communityApps</c> list), a provider's build-info image (Moonfin, Litefin, …), or the
    /// Jellyfin default for anything Jellyfin. Null when neither has anything for it.
    /// </summary>
    public static class CatalogPreviews
    {
        private static IReadOnlyDictionary<string, string> _community =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string? For(ProviderManifest? manifest, GitHubRelease release)
        {
            if (Https(release.PreviewImage) is { } own)
                return own;
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

        /// <summary>
        /// Takes the previews from the official community catalog, by app name (and group name), for
        /// the lists that only know an app by the name in the packages README (the build-info view).
        /// Called next to <see cref="AppCategories.Use"/>; no catalog at all keeps what was there.
        /// </summary>
        public static void Use(CommunityCatalog? catalog)
        {
            if (catalog is null)
                return;
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in catalog.Apps)
            {
                if (Https(entry.PreviewImage) is not { } url)
                    continue;
                foreach (var key in new[] { entry.Name, entry.Group })
                    if (!string.IsNullOrWhiteSpace(key))
                        byName.TryAdd(key.Trim(), url);
            }
            _community = byName;
        }

        /// <summary>The catalog's preview for the app with exactly this name or group, or null.</summary>
        public static string? Community(string? name) =>
            !string.IsNullOrWhiteSpace(name) && _community.TryGetValue(name.Trim(), out var url) ? url : null;

        /// <summary>The value when it is an absolute HTTPS URL, else null.</summary>
        public static string? Https(string? value) =>
            Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri.OriginalString
                : null;
    }
}
