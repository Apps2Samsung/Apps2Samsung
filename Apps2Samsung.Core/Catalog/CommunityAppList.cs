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
    /// a group fold into one entry whose assets are offered as variants. The package's description
    /// rides along for the app sheet (#835); a group takes the description of its first file.
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
                    var category = AppCategories.Clean(entry?.Category);
                    // Kept only when this version can write it (UserSettingsSpec.IsUsable).
                    asset.UserSettings = entry?.UserSettings is { IsUsable: true } spec ? spec : null;
                    asset.PackageVersion = Clean(entry?.Version) ?? asset.PackageVersion;
                    asset.AppId = Clean(entry?.AppId) ?? asset.AppId;
                    asset.PackageId = Clean(entry?.PackageId) ?? asset.PackageId;
                    asset.RequiredVersion = Clean(entry?.RequiredVersion) ?? asset.RequiredVersion;
                    asset.IsClosedSource = entry?.IsClosedSource == true;
                    asset.IsSelfUpdating = entry?.IsSelfUpdating == true;
                    asset.CatalogRepo = Clean(entry?.Repo);
                    asset.MovedTo = entry?.MovedTo is { Url.Length: > 0, Fingerprint.Length: > 0 } moved ? moved : null;

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
                                Description = Clean(entry.Description) ?? string.Empty,
                                PreviewImage = CatalogPreviews.Https(entry.PreviewImage),
                            };
                            SourceRepo.Stamp(group, entry.Repo, entry.Host);
                            groups[entry.Group] = group;
                        }
                        group.Assets.Add(asset);
                        // A group is adult when any of its files is (#850), and needs Partner when any does.
                        group.IsAdult |= entry.Adult;
                        group.IsRoot |= entry.IsRoot;
                        // Closed source per file (#1016): Visible hides only those files, not the group.
                        group.IsClosedSource |= entry.IsClosedSource;
                        group.IsSelfUpdating |= entry.IsSelfUpdating;
                        group.RequiresPartner |= entry.RequiresPartner;
                        group.PreviewImage ??= CatalogPreviews.Https(entry.PreviewImage);
                        continue;
                    }

                    var single = new GitHubRelease
                    {
                        // The catalog's display name when it has one ("AquaPlay IPTV"), else the file name as before.
                        Name = string.IsNullOrWhiteSpace(entry?.Name) ? Path.GetFileNameWithoutExtension(asset.FileName) : entry!.Name,
                        TagName = r.TagName,
                        PublishedAt = r.PublishedAt,
                        Url = r.Url,
                        Assets = new List<Asset> { asset },
                        // The provider's level, or the package's own cert_level (TV-Store #50).
                        RequiresPartner = requiresPartner || entry?.RequiresPartner == true,
                        Category = category,
                        Description = Clean(entry?.Description) ?? string.Empty,
                        IsAdult = entry?.Adult == true,
                        IsRoot = entry?.IsRoot == true,
                        IsClosedSource = entry?.IsClosedSource == true,
                        IsSelfUpdating = entry?.IsSelfUpdating == true,
                        PreviewImage = CatalogPreviews.Https(entry?.PreviewImage),
                    };
                    if (entry != null)
                        SourceRepo.Stamp(single, entry.Repo, entry.Host);
                    singles.Add(single);
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

        /// <summary>
        /// The apps the catalog stopped distributing (#1033), kept apart from <see cref="Expand"/>: they
        /// never reach the list, its categories or its counts. See <see cref="RemovedApps"/>.
        /// </summary>
        public static IReadOnlyList<CommunityCatalogRemoved> Removed(CommunityCatalog? catalog) => RemovedApps.From(catalog);

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// The entries the user gets to see (#850): everything when "Show adult (18+) apps" is on,
        /// otherwise the list without the adult entries. Both heads and the CLI run their list
        /// through this before any grouping, count, category filter or search, so with the setting
        /// off nothing in the UI shows that those entries exist. "Show root apps" works the same way
        /// for entries flagged root, but is on by default. "Show closed-source apps" (#1016, on by
        /// default) works per file instead: with it off, a grouped app loses only its closed-source
        /// variants and stays while it has others; an app whose every file is closed source goes.
        /// </summary>
        public static IEnumerable<GitHubRelease> Visible(IEnumerable<GitHubRelease> entries, bool showAdult, bool showRoot = true, bool showClosedSource = true)
        {
            foreach (var e in entries)
            {
                if ((!showAdult && e.IsAdult) || (!showRoot && e.IsRoot))
                    continue;
                if (!showClosedSource && e.IsClosedSource)
                {
                    // A single file flagged closed source has nothing left; a group keeps its open files.
                    if (e.HasVariants && e.WithoutClosedSourceVariants() is { } open)
                        yield return open;
                    continue;
                }
                yield return e;
            }
        }

        /// <summary>The entries in <paramref name="category"/> (<see cref="AppCategories.All"/> keeps everything).</summary>
        public static IEnumerable<GitHubRelease> Filter(IEnumerable<GitHubRelease> entries, string category)
            => category == AppCategories.All
                ? entries
                : entries.Where(e => AppCategories.Normalize(e.Category) == AppCategories.Normalize(category));

        /// <summary>How many entries each category has, in dropdown order, with "All" first. Empty categories are left out.</summary>
        public static IReadOnlyList<(string Id, int Count)> Counts(IReadOnlyCollection<GitHubRelease> entries)
        {
            var counts = new List<(string, int)> { (AppCategories.All, entries.Count) };
            foreach (var id in AppCategories.Ordered)
            {
                var n = entries.Count(e => AppCategories.Normalize(e.Category) == id);
                if (n > 0)
                    counts.Add((id, n));
            }
            return counts;
        }
    }
}
