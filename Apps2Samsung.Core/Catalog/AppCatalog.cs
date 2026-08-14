using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core; // AddLatestRelease
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Catalog
{
    /// <summary>One installable app offered in the customization editor (icon / title).</summary>
    /// <param name="DisplayName">Human-readable name shown in the list.</param>
    /// <param name="Key">Token matched (case-insensitive substring) against the package file name at install.</param>
    /// <param name="HasOblong">True when this head ships a bundled 16:9 "oblong" launcher tile for this app.</param>
    public sealed record AppCatalogEntry(string DisplayName, string Key, bool HasOblong);

    /// <summary>
    /// Builds the shared list of installable apps for the icon/title customization editor, so both heads
    /// present the same catalog instead of each reimplementing it (#521). The head fetches the
    /// <see cref="ProviderManifest"/> its own way (desktop <c>ProviderManifestService</c> / mobile
    /// <c>CatalogService</c>) and passes it in; this class does the shared shaping — expanding the
    /// community bundle to one entry per .wgt asset, collapsing the Jellyfin variants into one entry, and
    /// flagging which apps have a bundled oblong tile via <see cref="IOblongIconSource"/>.
    /// </summary>
    public sealed class AppCatalog
    {
        private readonly AddLatestRelease _releases;
        private readonly IOblongIconSource _oblong;

        public AppCatalog(AddLatestRelease releases, IOblongIconSource oblong)
        {
            _releases = releases;
            _oblong = oblong;
        }

        public async Task<IReadOnlyList<AppCatalogEntry>> BuildAsync(ProviderManifest manifest)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<AppCatalogEntry>();

            void Add(string display, string key)
            {
                key = key?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(key) || !seen.Add(key))
                    return;

                entries.Add(new AppCatalogEntry(
                    DisplayName: string.IsNullOrWhiteSpace(display) ? key : display.Trim(),
                    Key: key,
                    HasOblong: _oblong.TryGetOblong(key) != null));
            }

            foreach (var provider in manifest.Providers)
            {
                if (provider.ExpandAssets)
                {
                    // Community bundle: one entry per real .wgt asset in the latest release, so every
                    // community app is offered and the list stays current with the repo (see #462).
                    // Custom icons/titles only apply to web apps, so skip native .tpk assets. Best-effort:
                    // a fetch failure just yields no community rows rather than breaking the editor.
                    if (string.IsNullOrWhiteSpace(provider.Url))
                        continue;
                    try
                    {
                        var releases = await _releases.GetReleasesAsync(
                            provider.Url, provider.Prefix, provider.DisplayName, provider.Take);
                        foreach (var r in releases)
                            foreach (var asset in r.Assets)
                            {
                                if (!asset.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
                                    continue;
                                var name = Path.GetFileNameWithoutExtension(asset.FileName);
                                Add(name, name);
                            }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[AppCatalog] Could not expand community assets from {provider.Url}: {ex.Message}");
                    }
                    continue;
                }

                // All Jellyfin builds share the "Jellyfin" file-name root, so collapse the variants
                // (AVPlay, Legacy, …) into one entry that matches any of them.
                if (string.IsNullOrWhiteSpace(provider.DisplayName) ||
                    provider.DisplayName.StartsWith("Jellyfin", StringComparison.OrdinalIgnoreCase))
                    Add("Jellyfin", "Jellyfin");
                else
                    Add(provider.DisplayName, provider.DisplayName);
            }

            return entries;
        }
    }
}
