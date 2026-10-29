using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Configuration;
using Apps2Samsung.Packaging;

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
    /// </summary>
    public sealed record PackageIdPolicy(string TvDuid, bool NewId = false, bool OwnId = false);

    public static class PackageIds
    {
        /// <summary>
        /// Renames <paramref name="wgtPath"/> in place when the policy or the store says so, and
        /// records a new rename. Returns the rename applied, or null when the package keeps its id.
        /// Call before the patchers and the re-sign: the rewrite drops the package's signatures.
        /// </summary>
        public static async Task<PackageIdRename?> ApplyAsync(string wgtPath, PackageIdPolicy? policy, IAppConfig config, Action<string>? progress = null)
        {
            if (policy is null || string.IsNullOrWhiteSpace(policy.TvDuid))
                return null;

            var (originalAppId, originalPackageId) = await WgtManifest.ReadIdsAsync(wgtPath);
            if (!PackageIdRewriter.IsValidPackageId(originalPackageId))
                return null;

            // A retry on the same working file (a certificate countdown, a [118] recovery) sees the id
            // it was already given; nothing more to do.
            if (PackageIdRenameStore.Read(config.PackageIdRenamesJson).Any(r =>
                    string.Equals(r.TvDuid, policy.TvDuid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.NewPackageId, originalPackageId, StringComparison.Ordinal)))
                return null;

            // Back to the package's own id on this TV: drop the record, so neither this install nor
            // any later one reuses the renamed id. With NewId as well, a fresh id follows below.
            if (policy.OwnId && PackageIdRenameStore.Find(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId) is { } forgotten)
            {
                config.PackageIdRenamesJson = PackageIdRenameStore.Remove(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId);
                Trace.WriteLine($"[PackageId] Forgot the rename {forgotten.OriginalPackageId} -> {forgotten.NewPackageId} for this TV; installing under {originalPackageId}.");
                progress?.Invoke($"Package id {originalPackageId} on this TV again (the rename to {forgotten.NewPackageId} was forgotten)");
            }

            var remembered = PackageIdRenameStore.Find(config.PackageIdRenamesJson, policy.TvDuid, originalPackageId);
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
            else
            {
                return null;
            }

            if (rename is null)
                return null;

            var (newAppId, _) = await WgtManifest.ReadIdsAsync(wgtPath);
            progress?.Invoke($"Package id {rename.OldId} → {rename.NewId} on this TV");
            if (remembered is null || !string.Equals(remembered.NewPackageId, rename.NewId, StringComparison.Ordinal) || string.IsNullOrEmpty(remembered.NewAppId))
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
    }
}
