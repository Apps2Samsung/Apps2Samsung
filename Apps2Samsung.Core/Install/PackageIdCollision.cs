using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Install
{
    /// <summary>
    /// Another app on the TV already uses the package id of the package being installed (#1028): a
    /// fork that kept upstream's id (tacplayer ships the Jellyfin client's <c>AprZAARz4r</c>).
    /// Installing it under that id would replace the other app and its data.
    /// </summary>
    /// <param name="Occupant">The app on the TV that has the id.</param>
    /// <param name="PackageName">The name of the package being installed, as its manifest gives it.</param>
    /// <param name="IsNative">A .tpk: renaming is off the table (native code can hardcode its own
    /// paths), so the user decides between Cancel and Replace.</param>
    public sealed record PackageIdCollision(InstalledApp Occupant, string PackageName, bool IsNative)
    {
        /// <summary>The name the TV lists the other app under; its id when it lists none.</summary>
        public string OccupantName => Occupant.DisplayName;
    }

    /// <summary>
    /// Finds a package-id collision on a TV and the id a colliding web package is installed under
    /// instead (#1028). That id is derived from the package's catalog entry, never random, so every
    /// client (this one on any head, and TV-Store, Apps2Samsung/TV-Store#84) renames the same package
    /// to the same id and can update what the other installed.
    /// </summary>
    public static class PackageIdCollisions
    {
        /// <summary>Tizen web package ids are ten alphanumeric characters.</summary>
        public const int IdLength = 10;

        /// <summary>Versions the derivation: a change to it must change this, or clients disagree.</summary>
        public const string Salt = "a2s-pkgid-v1:";

        private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>
        /// The package id for <paramref name="catalogKey"/>: the first ten characters of
        /// base62(sha256(<see cref="Salt"/> + key)), one character per byte (<c>byte % 62</c>),
        /// alphabet <c>0-9A-Za-z</c>. The exact scheme TV-Store implements; do not change one side alone.
        /// </summary>
        public static string DerivedId(string catalogKey)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Salt + catalogKey));
            var id = new char[IdLength];
            for (int i = 0; i < IdLength; i++)
                id[i] = Alphabet[hash[i] % Alphabet.Length];
            return new string(id);
        }

        /// <summary>
        /// The catalog key for one file of a catalog entry: <c>&lt;repo&gt;/&lt;file&gt;</c>. The repo is
        /// the community manifest's <c>repo</c> ("owner/repo"), the repository URL for an app from an
        /// independent repository (#1018), else the GitHub repository the entry's releases come from.
        /// Stable across versions: none of these change when the app does. Null when there is nothing
        /// to key on.
        /// </summary>
        public static string? CatalogKey(GitHubRelease? release, Asset? asset)
        {
            if (release is null || asset is null || string.IsNullOrWhiteSpace(asset.FileName))
                return null;

            string? repo = release.Source.Kind == CatalogSourceKind.Repository
                ? release.Source.Url?.Trim()
                : asset.CatalogRepo?.Trim() ?? RepoSlug(release.SourceUrl);
            if (string.IsNullOrWhiteSpace(repo))
                repo = string.IsNullOrWhiteSpace(release.Source.Name) ? null : release.Source.Name.Trim();

            return repo is null ? null : repo + "/" + asset.FileName.Trim();
        }

        /// <summary>
        /// The key for a package that comes from no catalog entry (a file the user picked): its own
        /// package id and name, so the same package still gets the same id on every install.
        /// </summary>
        public static string LocalKey(string packageId, string? packageName) =>
            "local/" + packageId + "/" + (packageName ?? string.Empty).Trim();

        // "https://github.com/owner/repo" → "owner/repo".
        private static string? RepoSlug(string? sourceUrl)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
                return null;
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[0] + "/" + parts[1] : null;
        }

        /// <summary>The package part of an installed app's id ("Pkg" of "Pkg.App"); a native app id as-is.</summary>
        public static bool BelongsTo(InstalledApp app, string packageId) =>
            string.Equals(app.TizenId, packageId, StringComparison.Ordinal)
            || app.TizenId.StartsWith(packageId + ".", StringComparison.Ordinal);

        /// <summary>
        /// Whether a name the TV lists matches one of the package's own names: case-insensitive,
        /// trimmed. A same-named app is the same app (an update, or a variant of one group like the
        /// Moonlight forks) and installs over it as always.
        /// </summary>
        public static bool SameName(string? title, IEnumerable<string> names) =>
            !string.IsNullOrWhiteSpace(title)
            && names.Any(n => string.Equals(n.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The app on the TV that would be replaced by a different app, or null. Only an app the TV
        /// lists with a name counts: without one there is no telling it is another app, and renaming
        /// on a guess would turn every update of such an app into a second tile.
        /// </summary>
        public static PackageIdCollision? Find(IEnumerable<InstalledApp>? installed, PackageNames package)
        {
            if (installed is null || string.IsNullOrWhiteSpace(package.PackageId) || package.Names.Count == 0)
                return null;

            var mine = installed.Where(a => BelongsTo(a, package.PackageId!)).ToList();
            // Any app of the package listed under one of our names: it is ours (a multi-app package
            // can list a service under another title).
            if (mine.Count == 0 || mine.Any(a => SameName(a.Title, package.Names)))
                return null;

            var other = mine.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Title));
            return other is null ? null : new PackageIdCollision(other, package.Names[0], package.IsNative);
        }

        /// <summary>Whether the TV already has an app under <paramref name="packageId"/>.</summary>
        public static bool IsInstalled(IEnumerable<InstalledApp>? installed, string packageId) =>
            installed?.Any(a => BelongsTo(a, packageId)) == true;
    }
}
