using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>
    /// The other app ids that ship inside the same package as an installed app.
    ///
    /// A .wgt may package a service next to its UI (<c>NuvioTV001.EngineFsService</c> beside
    /// <c>NuvioTV001.NuvioTV</c>). The service runs in its own process, so the app's own console never
    /// shows a word of it — when it fails to come up, the UI reports only what it sees from the
    /// outside, typically a refused connection to the port the service should be listening on. Its id
    /// is what makes the service reachable: debug mode, and with it a console, is per app id.
    /// </summary>
    public static class TizenPackageServices
    {
        /// <summary>
        /// The package part of a Tizen app id: <c>NuvioTV001.EngineFsService</c> → <c>NuvioTV001</c>.
        /// An id without a dot is its own package, which is how the TV reports a few built-ins.
        /// </summary>
        public static string PackageIdOf(string? tizenId)
        {
            var id = tizenId?.Trim() ?? string.Empty;
            var dot = id.IndexOf('.');
            return dot > 0 ? id[..dot] : id;
        }

        /// <summary>
        /// The ids installed under the same package as <paramref name="tizenId"/>, itself excluded.
        /// Ids that name themselves a service come first — they are what a user opening this is after —
        /// and the rest follow alphabetically.
        /// </summary>
        /// <remarks>
        /// The TV's <c>vd_applist</c> does not promise to list a packaged service at all, so an empty
        /// result is not proof there is none: callers offer this as a suggestion, never as the only way
        /// to name one.
        /// </remarks>
        public static IReadOnlyList<string> SiblingIdsOf(IEnumerable<InstalledApp>? installed, string tizenId)
        {
            if (installed is null || string.IsNullOrWhiteSpace(tizenId))
                return Array.Empty<string>();

            var package = PackageIdOf(tizenId);
            if (string.IsNullOrEmpty(package))
                return Array.Empty<string>();

            return installed
                .Select(app => app.TizenId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Where(id => !string.Equals(id, tizenId, StringComparison.OrdinalIgnoreCase))
                .Where(id => string.Equals(PackageIdOf(id), package, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(LooksLikeService)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Whether an id names itself a service, which is the convention every packaged one follows.</summary>
        public static bool LooksLikeService(string id) =>
            id.Contains("service", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("daemon", StringComparison.OrdinalIgnoreCase);
    }
}
