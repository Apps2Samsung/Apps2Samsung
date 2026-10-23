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
        /// <summary>The catalog entry <paramref name="app"/> came from, or null when no listed file declares its id.</summary>
        public static CatalogMatch? Find(InstalledApp app, IEnumerable<GitHubRelease> catalog)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.TizenId))
                return null;

            foreach (var release in catalog)
            {
                foreach (var asset in release.Assets)
                {
                    if (!string.IsNullOrWhiteSpace(asset.AppId) &&
                        string.Equals(asset.AppId, app.TizenId, StringComparison.OrdinalIgnoreCase))
                        return new CatalogMatch(release, asset, Compare(app.Version, asset.PackageVersion));
                }
            }
            return null;
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
