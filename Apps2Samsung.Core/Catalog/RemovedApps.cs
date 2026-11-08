using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Install;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The apps the community catalog stopped distributing (#1033, <c>removed</c> in
    /// <c>catalog.json</c>): which installed app is one of them, which of them a search names, and
    /// the text that says why. Removed entries are never installable and never listed with the
    /// catalog's apps, its categories or its counts; they only explain an installed copy that no
    /// longer updates, and a search that would otherwise find nothing.
    /// </summary>
    public static class RemovedApps
    {
        /// <summary>The badge on an installed app that matches a removed entry.</summary>
        public const string BadgeKey = "lblRemovedBadge";

        /// <summary>The line under the reason: the installed copy keeps working, without updates.</summary>
        public const string FooterKey = "lblRemovedFooter";

        /// <summary>A search shorter than this does not list removed entries (one letter would list them all).</summary>
        public const int MinSearchLength = 2;

        /// <summary>The removed entries with a name, from the official catalog's <paramref name="catalog"/>.</summary>
        public static IReadOnlyList<CommunityCatalogRemoved> From(CommunityCatalog? catalog) =>
            (catalog?.Removed ?? new List<CommunityCatalogRemoved>())
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name))
                .ToList();

        /// <summary>The en.json key for <paramref name="reason"/>; an unknown or missing reason gets the generic text.</summary>
        public static string ReasonKey(string? reason) => (reason ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "unlicensed-content" => "lblRemovedReasonUnlicensedContent",
            "unlicensed-service" => "lblRemovedReasonUnlicensedService",
            "unlicensed-game-data" => "lblRemovedReasonUnlicensedGameData",
            "takedown" => "lblRemovedReasonTakedown",
            "superseded" => "lblRemovedReasonSuperseded",
            "upstream-gone" => "lblRemovedReasonUpstreamGone",
            _ => "lblRemovedReasonUnknown",
        };

        /// <summary>
        /// The removed entry <paramref name="app"/> is a copy of, or null. Matched on the ids its files
        /// had while they shipped (<c>package_ids</c>, <c>app_ids</c>), through the rename store for a
        /// copy installed under another id (#839); else on the id derived from its repo and files, for a
        /// copy installed under that derived id (#1028). An id match is ignored when a file still in
        /// <paramref name="liveCatalog"/> declares the same id (a fork that kept the removed app's id,
        /// or the other way round): the TV cannot say which of the two it has, and calling a live app
        /// removed is worse than missing a removed one. A derived id names one entry, so it always counts.
        /// </summary>
        /// <param name="packageIdRenamesJson">The heads' rename store (#839), as <see cref="InstalledAppUpdates.Find"/> takes it.</param>
        public static CommunityCatalogRemoved? Find(InstalledApp app, IReadOnlyList<CommunityCatalogRemoved> removed,
            IEnumerable<GitHubRelease>? liveCatalog = null, string? packageIdRenamesJson = null)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.TizenId) || removed is null || removed.Count == 0)
                return null;

            var derived = DerivedMatch(app.TizenId, removed);
            if (derived is not null)
                return derived;

            var appId = Apps2Samsung.Packaging.PackageIdRenameStore.OriginalAppIdOf(packageIdRenamesJson, app.TizenId) ?? app.TizenId;
            var package = PackagePart(appId);

            var byId = removed.FirstOrDefault(r =>
                (r.AppIds?.Any(id => string.Equals(id?.Trim(), appId, StringComparison.OrdinalIgnoreCase)) ?? false)
                || (r.PackageIds?.Any(id => !string.IsNullOrWhiteSpace(id) && string.Equals(id.Trim(), package, StringComparison.Ordinal)) ?? false));
            if (byId is null)
                return null;

            var stillListed = (liveCatalog ?? Enumerable.Empty<GitHubRelease>())
                .SelectMany(r => r.Assets)
                .Any(a => string.Equals(a.AppId, appId, StringComparison.OrdinalIgnoreCase)
                       || (!string.IsNullOrWhiteSpace(a.PackageId) && string.Equals(a.PackageId, package, StringComparison.Ordinal)));
            return stillListed ? null : byId;
        }

        /// <summary>
        /// The removed entries a search for <paramref name="query"/> names: every word of the query in
        /// the entry's name, case-insensitive, the way the CLI finds an app. Nothing for a query
        /// shorter than <see cref="MinSearchLength"/>. Sorted by name.
        /// </summary>
        public static IReadOnlyList<CommunityCatalogRemoved> Search(string? query, IEnumerable<CommunityCatalogRemoved> removed)
        {
            var q = (query ?? string.Empty).Trim();
            if (q.Length < MinSearchLength || removed is null)
                return Array.Empty<CommunityCatalogRemoved>();
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return removed
                .Where(r => !string.IsNullOrWhiteSpace(r.Name) && words.All(w => r.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The removed entry named exactly <paramref name="name"/> (case-insensitive), or null.</summary>
        public static CommunityCatalogRemoved? Named(string? name, IEnumerable<CommunityCatalogRemoved> removed) =>
            string.IsNullOrWhiteSpace(name) || removed is null
                ? null
                : removed.FirstOrDefault(r => string.Equals(r.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

        // "Pkg" of "Pkg.App"; a native app id (no package part of the web form) as-is.
        private static string PackagePart(string appId)
        {
            var dot = appId.IndexOf('.');
            return dot == PackageIdCollisions.IdLength ? appId[..dot] : appId;
        }

        // An installed id whose package part is the id derived from one of the entry's files (#1028).
        private static CommunityCatalogRemoved? DerivedMatch(string tizenId, IReadOnlyList<CommunityCatalogRemoved> removed)
        {
            var dot = tizenId.IndexOf('.');
            if (dot != PackageIdCollisions.IdLength)
                return null;
            var package = tizenId[..dot];
            foreach (var entry in removed)
            {
                if (string.IsNullOrWhiteSpace(entry.Repo) || entry.Files is null)
                    continue;
                foreach (var file in entry.Files)
                {
                    if (string.IsNullOrWhiteSpace(file))
                        continue;
                    if (string.Equals(PackageIdCollisions.DerivedId(entry.Repo.Trim() + "/" + file.Trim()), package, StringComparison.Ordinal))
                        return entry;
                }
            }
            return null;
        }
    }
}
