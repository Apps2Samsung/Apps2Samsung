using System;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
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

        /// <summary>Whether the TV lists TV-Store, read off <c>vd_applist</c> like the debug agent's check.</summary>
        public static async Task<bool> IsInstalledAsync(ISdbEngine sdb, string tvIp)
        {
            ArgumentNullException.ThrowIfNull(sdb);
            var listed = await sdb.AppsAsync(tvIp).ConfigureAwait(false);
            return TizenInstalledApps.Parse(listed.Output)
                .Any(a => string.Equals(a.TizenId, AppId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
