using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.TvStore
{
    /// <summary>
    /// The Apps2Samsung TV-Store app (#844): the catalog on the TV itself, installing packages
    /// natively through the set's own sdbd. Built and released from the Apps2Samsung/TV-Store
    /// repository; installed through a head like any package, which is also what makes the
    /// certificate it signs with: a head hands that over once (<see cref="TvStoreClient.ArmAsync"/>).
    /// </summary>
    public static class TvStorePackage
    {
        /// <summary>The Tizen package id, as <c>vd_applist</c> lists it under <c>app_package_name</c>.</summary>
        public const string PackageId = "A2STVStore";

        /// <summary>
        /// The Tizen application id TV-Store is built with: what <c>0 debug</c> and <c>0 was_kill</c>
        /// address on a set that has it under its own id. A set may list it under another one
        /// (<see cref="TvStoreInstallation.AppId"/>), see <see cref="Find"/>.
        /// </summary>
        public const string AppId = "A2STVStore.Store";

        /// <summary>
        /// The application part of <see cref="AppId"/>. An install under a new package id (#839)
        /// rewrites the package part only, so a renamed TV-Store is still <c>Xxxxxxxxxx.Store</c>.
        /// </summary>
        public const string AppName = "Store";

        /// <summary>
        /// config.xml's <c>&lt;name&gt;</c>, which <c>vd_applist</c> reports as <c>app_title</c>. A
        /// rename leaves it alone: it is how a TV-Store renamed by another device is told apart.
        /// </summary>
        public const string Title = "Apps2Samsung TV-Store";

        /// <summary>Where the package comes from, for a status line that tells the user to install it first.</summary>
        public const string RepositoryUrl = "https://github.com/Apps2Samsung/TV-Store";

        /// <summary>
        /// The application id TV-Store 0.3.0 shipped under, before it became <see cref="AppId"/> in 0.3.5.
        /// A set that lists only this has a TV-Store too old to arm, not none at all.
        /// </summary>
        public const string LegacyAppId = "A2SStore001.Store";

        /// <summary>
        /// TV-Store's own catalog entry, for a head that wants to send the user there when the TV
        /// has no TV-Store yet (#888): the release whose package carries <see cref="PackageId"/>.
        /// Null when the catalog has none, or when the catalog hasn't loaded.
        /// </summary>
        public static GitHubRelease? FindInCatalog(IEnumerable<GitHubRelease> releases) =>
            releases.FirstOrDefault(r => r.Assets.Any(a => string.Equals(a.PackageId, PackageId, StringComparison.Ordinal)));

        /// <summary>Whether the TV lists TV-Store, read off <c>vd_applist</c> like the debug agent's check.</summary>
        /// <param name="packageIdRenamesJson">This device's package-id renames (#839), so a TV-Store it installed under another id counts.</param>
        /// <exception cref="TvStoreUnreachableException">The TV sent no listing (#889).</exception>
        public static async Task<bool> IsInstalledAsync(ISdbEngine sdb, string tvIp, string? packageIdRenamesJson = null) =>
            (await FindAsync(sdb, tvIp, packageIdRenamesJson).ConfigureAwait(false)).IsInstalled;

        /// <summary>
        /// Which TV-Store the TV lists, if any. A failed <c>vd_applist</c> throws instead of reading as
        /// "nothing installed": an sdbd that resets the connection used to send the user off to
        /// reinstall a TV-Store that was there all along (#889).
        /// </summary>
        /// <param name="packageIdRenamesJson">This device's package-id renames (#839), so a TV-Store it installed under another id counts.</param>
        /// <exception cref="TvStoreUnreachableException">The TV sent no listing (#889).</exception>
        public static async Task<TvStoreInstallState> GetInstallStateAsync(ISdbEngine sdb, string tvIp, string? packageIdRenamesJson = null) =>
            (await FindAsync(sdb, tvIp, packageIdRenamesJson).ConfigureAwait(false)).State;

        /// <summary>
        /// The TV-Store the TV lists, with the application id it lists it under, which is what every
        /// later verb (<c>0 was_kill</c>, <c>0 debug</c>, the REST close) must address.
        /// </summary>
        /// <param name="packageIdRenamesJson">This device's package-id renames (#839), so a TV-Store it installed under another id counts.</param>
        /// <exception cref="TvStoreUnreachableException">The TV sent no listing (#889).</exception>
        public static async Task<TvStoreInstallation> FindAsync(ISdbEngine sdb, string tvIp, string? packageIdRenamesJson = null)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            var listed = await sdb.AppsAsync(tvIp).ConfigureAwait(false);

            IReadOnlyList<InstalledApp> apps;
            try
            {
                apps = TizenInstalledApps.ParseOrThrow(listed);
            }
            catch (InvalidOperationException ex)
            {
                throw new TvStoreUnreachableException(ex.Message, ex);
            }

            return Find(apps, packageIdRenamesJson);
        }

        /// <summary>
        /// Picks TV-Store out of a TV's app list. Its own id first; then a copy installed under a new
        /// package id (#839), which the "On the TV" view already showed as TV-Store while the toolbox
        /// said "not installed" (a set listing <c>KUW3HHjMUR.Store</c>, titled Apps2Samsung TV-Store):
        /// known from this device's rename records, or, when another device did the renaming, from
        /// the application name and title a rename leaves untouched. Attaching verifies the page is
        /// TV-Store's anyway (<see cref="TvStoreClient.AttachAsync"/>), so a look-alike cannot be armed.
        /// </summary>
        public static TvStoreInstallation Find(IReadOnlyList<InstalledApp> apps, string? packageIdRenamesJson = null)
        {
            ArgumentNullException.ThrowIfNull(apps);

            var own = apps.FirstOrDefault(a => Is(a.TizenId, AppId));
            if (own is not null)
                return new TvStoreInstallation(TvStoreInstallState.Installed, own.TizenId);

            var renamed = apps.FirstOrDefault(a => IsRenamed(a, packageIdRenamesJson));
            if (renamed is not null)
            {
                Trace.WriteLine($"[tvstore] TV-Store is installed as {renamed.TizenId} (renamed from {AppId}, #839)");
                return new TvStoreInstallation(TvStoreInstallState.Installed, renamed.TizenId);
            }

            if (apps.Any(a => Is(a.TizenId, LegacyAppId)))
                return new TvStoreInstallation(TvStoreInstallState.Outdated, LegacyAppId);

            // The verdict users report: say what the TV did list, so the log answers the next report.
            Trace.WriteLine($"[tvstore] not installed: none of the {apps.Count} apps the TV listed is TV-Store ({string.Join(", ", apps.Select(a => a.TizenId))})");
            return new TvStoreInstallation(TvStoreInstallState.NotInstalled, null);
        }

        /// <summary>
        /// Whether <paramref name="app"/> is TV-Store under a package id other than its own (#839):
        /// one this device's rename records map back to <see cref="AppId"/>, or, failing a record
        /// (another head renamed it), the <see cref="AppName"/> application part with the <see cref="Title"/>.
        /// </summary>
        public static bool IsRenamed(InstalledApp app, string? packageIdRenamesJson)
        {
            ArgumentNullException.ThrowIfNull(app);
            if (string.IsNullOrWhiteSpace(app.TizenId) || Is(app.TizenId, AppId) || Is(app.TizenId, LegacyAppId))
                return false;

            if (Is(PackageIdRenameStore.OriginalAppIdOf(packageIdRenamesJson, app.TizenId), AppId))
                return true;

            var dot = app.TizenId.LastIndexOf('.');
            return dot > 0
                && Is(app.TizenId[(dot + 1)..], AppName)
                && string.Equals(app.Title?.Trim(), Title, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Is(string? id, string expected) => string.Equals(id, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What <see cref="TvStorePackage.Find"/> found on the TV.</summary>
    /// <param name="State">Whether a TV-Store that can be armed is on the set.</param>
    /// <param name="AppId">The application id the TV lists it under: <see cref="TvStorePackage.AppId"/>,
    /// the id of a copy installed under a new package id (#839), <see cref="TvStorePackage.LegacyAppId"/>
    /// for an outdated one, or null when there is none.</param>
    public sealed record TvStoreInstallation(TvStoreInstallState State, string? AppId)
    {
        /// <summary>A TV-Store that can be armed is on the set, listed as <see cref="AppId"/>.</summary>
        public bool IsInstalled => State == TvStoreInstallState.Installed;

        /// <summary>The set lists TV-Store under a package id other than its own (#839).</summary>
        public bool IsRenamed => IsInstalled && !string.Equals(AppId, TvStorePackage.AppId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What <see cref="TvStorePackage.GetInstallStateAsync"/> found on the TV.</summary>
    public enum TvStoreInstallState
    {
        /// <summary>The TV listed its apps, and TV-Store is not among them.</summary>
        NotInstalled,

        /// <summary>The TV lists <see cref="TvStorePackage.AppId"/>, or TV-Store under a new package id (#839).</summary>
        Installed,

        /// <summary>The TV lists only <see cref="TvStorePackage.LegacyAppId"/>: a TV-Store from before 0.3.5.</summary>
        Outdated,
    }
}
