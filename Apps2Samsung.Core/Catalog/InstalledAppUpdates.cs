using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>How the catalog's copy of an installed app compares to the one on the TV.</summary>
    public enum CatalogVersionState
    {
        /// <summary>The catalog carries no version for this file (a bundle built before versions were recorded).</summary>
        Unknown,
        /// <summary>Same version on the TV and in the catalog, or the TV is ahead of the catalog.</summary>
        UpToDate,
        /// <summary>The catalog's version is higher than the one on the TV.</summary>
        UpdateAvailable,
        /// <summary>The two differ but neither reads as a dotted version, so "newer" cannot be told.</summary>
        Different,
    }

    /// <summary>An installed app's entry in the catalog: the listed app, the exact file, and how their versions compare.</summary>
    public sealed record CatalogMatch(GitHubRelease Release, Asset Asset, CatalogVersionState State)
    {
        public bool HasUpdate => State is CatalogVersionState.UpdateAvailable or CatalogVersionState.Different;
        public string? CatalogVersion => Asset.PackageVersion;
    }

    /// <summary>
    /// Matches the apps a TV reports installed to the catalog, by the application id each file's
    /// manifest declares (recorded in the community catalog when the bundle is assembled, #820).
    /// The TV's app list reports the same manifest's version, so the comparison is between two
    /// readings of one field, never against a GitHub tag.
    /// </summary>
    public static class InstalledAppUpdates
    {
        /// <summary>
        /// The catalog entry <paramref name="app"/> came from, or null when no listed file declares its id.
        /// Several files can declare the same id: the forks of one app (seven Moonlight builds), or one
        /// app built per Tizen version. The TV cannot say which of them it has, so the comparison only
        /// claims what holds for every candidate: the installed version appearing among them means up
        /// to date; every candidate being newer means an update (the highest is named); anything else
        /// is left undecided rather than guessed, with the entry still matched for the Update button.
        /// </summary>
        /// <param name="packageIdRenamesJson">The heads' rename store (#839): an app installed under
        /// another id is matched by the id its package was built with, which is what the catalog records.</param>
        public static CatalogMatch? Find(InstalledApp app, IEnumerable<GitHubRelease> catalog, string? packageIdRenamesJson = null)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.TizenId))
                return null;

            var catalogId = Apps2Samsung.Packaging.PackageIdRenameStore.OriginalAppIdOf(packageIdRenamesJson, app.TizenId) ?? app.TizenId;

            var candidates = new List<(GitHubRelease Release, Asset Asset)>();
            foreach (var release in catalog)
            {
                foreach (var asset in release.Assets)
                {
                    if (!string.IsNullOrWhiteSpace(asset.AppId) &&
                        string.Equals(asset.AppId, catalogId, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((release, asset));
                }
            }
            if (candidates.Count == 0)
                return null;

            // With "Show all versions" on, an app lists several releases and every one of them
            // declares the same id. Only its newest release says anything about updates; the
            // older ones would read as "a candidate that is not newer" and block the verdict.
            var newestPerApp = candidates
                .GroupBy(c => c.Release.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(c => PublishedAt(c.Release)), StringComparer.OrdinalIgnoreCase);
            candidates = candidates
                .Where(c => PublishedAt(c.Release) == newestPerApp[c.Release.Name ?? string.Empty])
                .ToList();

            if (candidates.Count == 1)
                return new CatalogMatch(candidates[0].Release, candidates[0].Asset, Compare(app.Version, candidates[0].Asset.PackageVersion));

            var states = candidates.Select(c => (c, State: Compare(app.Version, c.Asset.PackageVersion))).ToList();
            var same = states.FirstOrDefault(x => x.State == CatalogVersionState.UpToDate &&
                string.Equals(Normalize(app.Version), Normalize(x.c.Asset.PackageVersion), StringComparison.OrdinalIgnoreCase));
            if (same.c.Asset is not null)
                return new CatalogMatch(same.c.Release, same.c.Asset, CatalogVersionState.UpToDate);

            if (states.All(x => x.State == CatalogVersionState.UpdateAvailable))
            {
                var newest = states
                    .OrderByDescending(x => TryParse(Normalize(x.c.Asset.PackageVersion), out var v) ? v : new Version(0, 0))
                    .First();
                return new CatalogMatch(newest.c.Release, newest.c.Asset, CatalogVersionState.UpdateAvailable);
            }

            return new CatalogMatch(candidates[0].Release, candidates[0].Asset, CatalogVersionState.Unknown);
        }

        /// <summary>
        /// Compares two manifest versions. Dotted numbers compare numerically ("2.1.0" is newer than
        /// "2.0.5", and "2.1" equals "2.1.0"); anything else only compares as equal or not.
        /// </summary>
        public static CatalogVersionState Compare(string? installed, string? catalog)
        {
            var have = Normalize(installed);
            var want = Normalize(catalog);
            if (want.Length == 0)
                return CatalogVersionState.Unknown;
            if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase))
                return CatalogVersionState.UpToDate;

            if (TryParse(have, out var haveVersion) && TryParse(want, out var wantVersion))
                return wantVersion > haveVersion ? CatalogVersionState.UpdateAvailable : CatalogVersionState.UpToDate;

            return CatalogVersionState.Different;
        }

        private static DateTimeOffset PublishedAt(GitHubRelease release) =>
            DateTimeOffset.TryParse(release.PublishedAt, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : DateTimeOffset.MinValue;

        private static string Normalize(string? s)
        {
            var t = (s ?? string.Empty).Trim();
            return t.Length > 1 && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1]) ? t.Substring(1) : t;
        }

        // Digits and dots only, one to four parts, padded to four so "2.1" and "2.1.0" compare
        // equal (System.Version treats a missing part as -1, not 0). Anything else ("2.1.0-beta",
        // "2026-10-23") is refused rather than guessed at.
        private static bool TryParse(string s, out Version version)
        {
            version = new Version(0, 0);
            if (s.Length == 0 || !s.All(c => char.IsDigit(c) || c == '.'))
                return false;
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0 || parts.Count > 4)
                return false;
            while (parts.Count < 4)
                parts.Add("0");
            return Version.TryParse(string.Join(".", parts), out version!);
        }
    }
}
