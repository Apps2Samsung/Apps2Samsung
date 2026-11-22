using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// What a newer catalog brought compared with the list on screen (#1157): the apps that were
    /// not there before, and how many of the others have a new version. Built from the lists the
    /// heads already show, so it says what the user will see change.
    /// </summary>
    public sealed record CatalogChanges(IReadOnlySet<string> NewApps, int NewVersions)
    {
        public static readonly CatalogChanges None = new(new HashSet<string>(), 0);

        public bool Any => NewApps.Count > 0 || NewVersions > 0;

        /// <summary>
        /// One app across reloads: its name and where it comes from. The versions of a provider that
        /// lists several (the Jellyfin history) share a key and count as one app.
        /// </summary>
        public static string Key(GitHubRelease release) =>
            $"{release.Name.Trim()}|{release.SourceUrl?.Trim()}".ToLowerInvariant();

        public bool IsNew(GitHubRelease release) => NewApps.Contains(Key(release));

        /// <summary>
        /// The version an entry stands for: the package versions its files carry, which is what the
        /// update check compares (#820). A release tag only when no file says its version, because
        /// the community bundle gets a new tag with every rebuild whether an app changed or not.
        /// </summary>
        private static string Version(GitHubRelease release)
        {
            var files = release.Assets
                .Where(a => !string.IsNullOrWhiteSpace(a.PackageVersion))
                .Select(a => $"{a.FileName}={a.PackageVersion}/{a.WebVersion}")
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToList();
            return files.Count > 0 ? string.Join(";", files) : release.TagName;
        }

        /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>. Nothing when there was no list before: a first load is not news.</summary>
        public static CatalogChanges Between(IEnumerable<GitHubRelease> before, IEnumerable<GitHubRelease> after)
        {
            var old = before.GroupBy(Key).ToDictionary(g => g.Key, g => g.Select(Version).ToHashSet(StringComparer.Ordinal));
            if (old.Count == 0)
                return None;

            var added = new HashSet<string>();
            int newVersions = 0;
            foreach (var app in after.GroupBy(Key))
            {
                if (!old.TryGetValue(app.Key, out var known))
                    added.Add(app.Key);
                else if (app.Select(Version).Any(v => !known.Contains(v)))
                    newVersions++;
            }
            return new CatalogChanges(added, newVersions);
        }

        /// <summary>
        /// Both sets of new apps, for the "new" label that stays for as long as the app is open.
        /// The version count is this refresh's only.
        /// </summary>
        public CatalogChanges After(CatalogChanges earlier) =>
            new(NewApps.Concat(earlier.NewApps).ToHashSet(), NewVersions);

        /// <summary>"Catalog updated: 2 new apps, 1 new version", from the head's catalog. Empty when nothing changed.</summary>
        public string Describe(Func<string, string> localize)
        {
            var parts = new List<string>();
            if (NewApps.Count == 1)
                parts.Add(localize("lblCatalogNewApp"));
            else if (NewApps.Count > 1)
                parts.Add(string.Format(localize("lblCatalogNewApps"), NewApps.Count));
            if (NewVersions == 1)
                parts.Add(localize("lblCatalogNewVersion"));
            else if (NewVersions > 1)
                parts.Add(string.Format(localize("lblCatalogNewVersions"), NewVersions));
            return parts.Count == 0 ? string.Empty : string.Format(localize("statusCatalogUpdated"), string.Join(", ", parts));
        }
    }
}
