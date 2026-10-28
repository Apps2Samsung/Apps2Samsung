using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Sdb
{
    /// <summary>How the "installed apps" view orders the TV's apps (#888).</summary>
    public enum InstalledAppOrder
    {
        /// <summary>Largest first, as <see cref="TizenInstalledApps.Parse"/> returns them.</summary>
        Size,

        /// <summary>Display name, A to Z.</summary>
        Name,

        /// <summary>Most recently installed first.</summary>
        Newest,
    }

    /// <summary>
    /// Orders a TV's apps for the "installed apps" view (#888). Apps the catalog has another version
    /// of always come first, so an update never sits halfway down a long list; within each group the
    /// chosen order applies, with the display name breaking ties.
    /// </summary>
    public static class InstalledAppSort
    {
        public static IReadOnlyList<InstalledApp> Sort(IEnumerable<InstalledApp> apps, InstalledAppOrder order)
        {
            ArgumentNullException.ThrowIfNull(apps);
            var updatesFirst = apps.OrderByDescending(a => a.HasUpdate);
            var sorted = order switch
            {
                InstalledAppOrder.Name => updatesFirst.ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                // An app without a readable date sinks to the bottom rather than reading as the oldest.
                InstalledAppOrder.Newest => updatesFirst
                    .ThenBy(a => InstalledAt(a) is null)
                    .ThenByDescending(a => InstalledAt(a))
                    .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                _ => updatesFirst
                    .ThenByDescending(a => a.SizeBytes)
                    .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            };
            return sorted.ToList();
        }

        // vd_applist's install_date, "2023-12-15 16:20:06" in the TV's own clock.
        private static DateTime? InstalledAt(InstalledApp app) =>
            DateTime.TryParseExact(app.InstallDate?.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var at)
                ? at
                : null;
    }
}
