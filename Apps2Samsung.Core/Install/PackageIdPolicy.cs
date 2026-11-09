using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Localization;
namespace Apps2Samsung.Install
{
    /// <summary>
    /// Which package id an install goes out under (#839). <see cref="NewId"/> asks for a fresh random
    /// id (the way out when the TV has the package's own id blocked, [118, -22]); the choice is
    /// stored per TV, so without it the install still applies a rename made earlier for this TV.
    /// <see cref="OwnId"/> undoes that: the rename remembered for this TV is forgotten and the
    /// package goes out under its own id again (a rename is otherwise for good: deleting the app
    /// on the TV and reinstalling it brought the remembered id straight back, TV-Store on a tester's
    /// set). The TV then shows a new tile next to the renamed copy, which stays until removed.
    /// <para>
    /// Without either, a web package whose id another app on the TV already has is renamed on its
    /// own (#1028), to the id <see cref="PackageIdCollisions.DerivedId"/> gives for
    /// <see cref="CatalogKey"/> (the catalog entry, <see cref="PackageIdCollisions.CatalogKey"/>);
    /// null for a package from no catalog entry, which is keyed on its own id and name instead.
    /// <see cref="OwnId"/> also turns that off: the user asked for the package's own id. An empty
    /// <see cref="TvDuid"/> (a TV that does not hand its DUID out) still gets the automatic rename,
    /// only nothing is remembered; the derived id finds the renamed copy again by itself.
    /// </para>
    /// </summary>
    public sealed record PackageIdPolicy(string TvDuid, bool NewId = false, bool OwnId = false, string? CatalogKey = null);

    public static class PackageIds
    {
        /// <summary>
        /// Renames <paramref name="wgtPath"/> in place when the policy, the store or a collision on
        /// the TV says so, and records a new rename. Returns the rename applied, or null when the
        /// package keeps its id. Call before the patchers and the re-sign: the rewrite drops the
        /// package's signatures, and before anything removes an old copy by id.
        /// </summary>
        /// <param name="installed">The TV's installed apps, read just before; null when they could not
        /// be read, which leaves the automatic rename out (there is nothing to collide with).</param>
        public static async Task<PackageIdRename?> ApplyAsync(string wgtPath, PackageIdPolicy? policy, IAppConfig config,
            Action<string>? progress = null, IReadOnlyList<InstalledApp>? installed = null)
        {
            if (policy is null)
                return null;

            var (originalAppId, originalPackageId) = await WgtManifest.ReadIdsAsync(wgtPath);
            if (!PackageIdRewriter.IsValidPackageId(originalPackageId))
                return null;

            var hasTv = !string.IsNullOrWhiteSpace(policy.TvDuid);

            // A retry on the same working file (a certificate countdown, a [118] recovery) sees the id
            // it was already given; nothing more to do.
            if (hasTv && PackageIdRenameStore.Read(config.PackageIdRenamesJson).Any(r =>
                    string.Equals(r.TvDuid, policy.TvDuid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.NewPackageId, originalPackageId, StringComparison.Ordinal)))
                return null;

            // Back to the package's own id on this TV: drop the record, so neither this install nor
            // any later one reuses the renamed id. With NewId as well, a fresh id follows below.
            if (hasTv && policy.OwnId && PackageIdRenameStore.Find(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId) is { } forgotten)
            {
                config.PackageIdRenamesJson = PackageIdRenameStore.Remove(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId);
                Trace.WriteLine($"[PackageId] Forgot the rename {forgotten.OriginalPackageId} -> {forgotten.NewPackageId} for this TV; installing under {originalPackageId}.");
                progress?.Invoke(ProgressText.Key("statusPackageIdForgotten", originalPackageId, forgotten.NewPackageId));
            }

            var remembered = hasTv ? PackageIdRenameStore.Find(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId) : null;
            PackageIdRename? rename;
            if (remembered is not null)
            {
                // The same id as last time on this TV, so the update lands on the existing tile.
                rename = await PackageIdRewriter.RenameAsync(wgtPath, remembered.NewPackageId);
            }
            else if (policy.NewId)
            {
                rename = await PackageIdRewriter.RandomizeAsync(wgtPath);
            }
            else if (!policy.OwnId && await AutomaticAsync(wgtPath, originalPackageId!, policy, installed) is { } automatic)
            {
                rename = await PackageIdRewriter.RenameAsync(wgtPath, automatic.NewId);
                if (rename is not null)
                {
                    rename = rename with { CollidedWith = automatic.CollidedWith };
                    if (automatic.CollidedWith is not null)
                        progress?.Invoke(ProgressText.Key("statusPackageIdSeparateApp", rename.NewId, automatic.CollidedWith));
                }
            }
            else
            {
                return null;
            }

            if (rename is null)
                return null;

            var (newAppId, _) = await WgtManifest.ReadIdsAsync(wgtPath);
            progress?.Invoke(ProgressText.Key("statusPackageIdRenamed", rename.OldId, rename.NewId));
            if (hasTv && (remembered is null || !string.Equals(remembered.NewPackageId, rename.NewId, StringComparison.Ordinal) || string.IsNullOrEmpty(remembered.NewAppId)))
            {
                config.PackageIdRenamesJson = PackageIdRenameStore.Set(config.PackageIdRenamesJson, new PackageIdRenameRecord
                {
                    TvDuid = policy.TvDuid,
                    OriginalPackageId = rename.OldId,
                    NewPackageId = rename.NewId,
                    OriginalAppId = originalAppId,
                    NewAppId = newAppId,
                    PackageFile = Path.GetFileName(wgtPath),
                    RenamedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            return rename;
        }

        private sealed record Automatic(string NewId, string? CollidedWith);

        /// <summary>
        /// The id a web package goes out under because of what is on the TV (#1028), or null to keep
        /// its own: the derived id when the TV already has a copy under it (installed from TV-Store,
        /// or by this client before the record was lost), or when another app has the package's id.
        /// </summary>
        private static async Task<Automatic?> AutomaticAsync(string wgtPath, string packageId, PackageIdPolicy policy, IReadOnlyList<InstalledApp>? installed)
        {
            if (installed is null)
                return null;

            var package = await WgtManifest.ReadNamesAsync(wgtPath);
            if (package.IsNative)
                return null;

            var key = string.IsNullOrWhiteSpace(policy.CatalogKey)
                ? PackageIdCollisions.LocalKey(packageId, package.Names.FirstOrDefault())
                : policy.CatalogKey!;
            var derived = PackageIdCollisions.DerivedId(key);
            if (string.Equals(derived, packageId, StringComparison.Ordinal))
                return null;

            var collision = PackageIdCollisions.Find(installed, package);
            if (collision is not null)
            {
                Trace.WriteLine($"[PackageId] {packageId} is taken on this TV by '{collision.OccupantName}' ({collision.Occupant.TizenId}); " +
                                $"installing '{collision.PackageName}' as {derived} (key {key}).");
                return new Automatic(derived, collision.OccupantName);
            }

            if (PackageIdCollisions.IsInstalled(installed, derived))
            {
                Trace.WriteLine($"[PackageId] This TV already has {packageId} under its derived id {derived} (key {key}); updating that copy.");
                return new Automatic(derived, null);
            }

            return null;
        }
    }
}
