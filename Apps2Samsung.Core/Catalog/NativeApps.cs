using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// Tells an installed native app (a .tpk: C, C++ or .NET) from a web app (a .wgt). The debug
    /// console rides the web inspector, which only a web app has: the TV launches a native app on
    /// `0 debug` and then never reports a port, so the heads say so before the app is restarted
    /// instead of after the wait.
    /// </summary>
    public static class NativeApps
    {
        /// <summary>Whether <paramref name="fileName"/> names a native package.</summary>
        public static bool IsNativePackage(string? fileName) =>
            !string.IsNullOrWhiteSpace(fileName) &&
            fileName.Trim().EndsWith(Constants.FilePatterns.TpkExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether <paramref name="app"/> is known to be native. The TV's app list does not say what
        /// kind of package an app came from, so this reads what the head knows: the file it installed
        /// on this TV (<see cref="InstalledVariantStore"/>), else the catalog files that declare the
        /// app's id. Without a record the catalog only counts when every such file is a .tpk, so a
        /// web app is never turned away over a native build that shares its id. False means "not
        /// known to be native", which is also the answer for an app installed from a file of the
        /// user's own.
        /// </summary>
        public static bool IsKnownNative(InstalledApp? app, IEnumerable<GitHubRelease>? catalog, string? packageIdRenamesJson = null,
            string? installedVariantsJson = null, string? tvIp = null, string? tvDuid = null)
        {
            if (app is null || string.IsNullOrWhiteSpace(app.TizenId))
                return false;

            var catalogId = PackageIdRenameStore.OriginalAppIdOf(packageIdRenamesJson, app.TizenId) ?? app.TizenId;

            var record = InstalledVariantStore.Find(installedVariantsJson, tvIp, catalogId, tvDuid);
            if (!string.IsNullOrWhiteSpace(record?.PackageFile))
                return IsNativePackage(record.PackageFile);

            var files = (catalog ?? Enumerable.Empty<GitHubRelease>())
                .SelectMany(release => release.Assets)
                .Where(asset => string.Equals(asset.AppId, catalogId, StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.FileName)
                .ToList();
            return files.Count > 0 && files.All(IsNativePackage);
        }
    }
}
