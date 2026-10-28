using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
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

        /// <summary>The Tizen application id: what <c>0 debug</c> and <c>0 was_kill</c> address.</summary>
        public const string AppId = "A2STVStore.Store";

        /// <summary>Where the package comes from, for a status line that tells the user to install it first.</summary>
        public const string RepositoryUrl = "https://github.com/Apps2Samsung/TV-Store";

        /// <summary>
        /// The application id TV-Store 0.3.0 shipped under, before it became <see cref="AppId"/> in 0.3.5.
        /// A set that lists only this has a TV-Store too old to arm, not none at all.
        /// </summary>
        public const string LegacyAppId = "A2SStore001.Store";

        /// <summary>Whether the TV lists TV-Store, read off <c>vd_applist</c> like the debug agent's check.</summary>
        /// <exception cref="TvStoreUnreachableException">The TV sent no listing (#889).</exception>
        public static async Task<bool> IsInstalledAsync(ISdbEngine sdb, string tvIp) =>
            await GetInstallStateAsync(sdb, tvIp).ConfigureAwait(false) == TvStoreInstallState.Installed;

        /// <summary>
        /// Which TV-Store the TV lists, if any. A failed <c>vd_applist</c> throws instead of reading as
        /// "nothing installed": an sdbd that resets the connection used to send the user off to
        /// reinstall a TV-Store that was there all along (#889).
        /// </summary>
        /// <exception cref="TvStoreUnreachableException">The TV sent no listing (#889).</exception>
        public static async Task<TvStoreInstallState> GetInstallStateAsync(ISdbEngine sdb, string tvIp)
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

            bool Lists(string id) => apps.Any(a => string.Equals(a.TizenId, id, StringComparison.OrdinalIgnoreCase));
            return Lists(AppId) ? TvStoreInstallState.Installed
                 : Lists(LegacyAppId) ? TvStoreInstallState.Outdated
                 : TvStoreInstallState.NotInstalled;
        }
    }

    /// <summary>What <see cref="TvStorePackage.GetInstallStateAsync"/> found on the TV.</summary>
    public enum TvStoreInstallState
    {
        /// <summary>The TV listed its apps, and TV-Store is not among them.</summary>
        NotInstalled,

        /// <summary>The TV lists <see cref="TvStorePackage.AppId"/>.</summary>
        Installed,

        /// <summary>The TV lists only <see cref="TvStorePackage.LegacyAppId"/>: a TV-Store from before 0.3.5.</summary>
        Outdated,
    }
}
