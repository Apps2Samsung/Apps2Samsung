using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Turns the community bundle release (one GitHub release, dozens of .wgt/.tpk assets) into
    /// the entries the installer lists, the same way for both heads. Without a catalog this is
    /// what the heads did inline: one entry per asset, named after the file. With the release's
    /// <c>catalog.json</c> every entry gets its display name and category, and files that share
    /// a group fold into one entry whose assets are offered as variants.
    /// </summary>
    public static class CommunityAppList
    {
        public static List<GitHubRelease> Expand(IEnumerable<GitHubRelease> releases, CommunityCatalog? catalog, bool requiresPartner)
        {
            var byFile = (catalog?.Apps ?? new List<CommunityCatalogEntry>())
                .Where(e => !string.IsNullOrWhiteSpace(e.File))
                .GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var singles = new List<GitHubRelease>();
            var groups = new Dictionary<string, GitHubRelease>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in releases)
            {
                foreach (var asset in r.Assets)
                {
                    byFile.TryGetValue(asset.FileName, out var entry);
                    var category = AppCategories.Normalize(entry?.Category);

                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Group))
                    {
                        asset.Variant = string.IsNullOrWhiteSpace(entry.Variant)
                            ? Path.GetFileNameWithoutExtension(asset.FileName)
                            : entry.Variant;

                        if (!groups.TryGetValue(entry.Group, out var group))
                        {
                            group = new GitHubRelease
                            {
                                Name = entry.Group,
                                TagName = r.TagName,
                                PublishedAt = r.PublishedAt,
                                Url = r.Url,
                                Assets = new List<Asset>(),
                                RequiresPartner = requiresPartner,
                                Category = category,
                                HasVariants = true,
                            };
                            groups[entry.Group] = group;
                        }
                        group.Assets.Add(asset);
                        continue;
                    }

                    singles.Add(new GitHubRelease
                    {
                        // The catalog's display name when it has one ("AquaPlay IPTV"), else the file name as before.
                        Name = string.IsNullOrWhiteSpace(entry?.Name) ? Path.GetFileNameWithoutExtension(asset.FileName) : entry!.Name,
                        TagName = r.TagName,
                        PublishedAt = r.PublishedAt,
                        Url = r.Url,
                        Assets = new List<Asset> { asset },
                        RequiresPartner = requiresPartner,
                        Category = category,
                    });
                }
            }

            foreach (var group in groups.Values)
            {
                group.Assets.Sort((a, b) => string.Compare(a.VariantLabel, b.VariantLabel, StringComparison.OrdinalIgnoreCase));
                // A group that ended up with one file is just an app with an odd name for its file; no picker needed.
                if (group.Assets.Count == 1)
                    group.HasVariants = false;
            }

            singles.AddRange(groups.Values);
            return singles;
        }

        /// <summary>The entries in <paramref name="category"/> (<see cref="AppCategories.All"/> keeps everything).</summary>
        public static IEnumerable<GitHubRelease> Filter(IEnumerable<GitHubRelease> entries, string category)
            => category == AppCategories.All
                ? entries
                : entries.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));

        /// <summary>How many entries each category has, in dropdown order, with "All" first. Empty categories are left out.</summary>
        public static IReadOnlyList<(string Id, int Count)> Counts(IReadOnlyCollection<GitHubRelease> entries)
        {
            var counts = new List<(string, int)> { (AppCategories.All, entries.Count) };
            foreach (var id in AppCategories.Ordered)
            {
                var n = entries.Count(e => string.Equals(e.Category, id, StringComparison.OrdinalIgnoreCase));
                if (n > 0)
                    counts.Add((id, n));
            }
            return counts;
        }
    }
}
