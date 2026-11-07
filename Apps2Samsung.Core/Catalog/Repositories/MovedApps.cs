using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog.Repositories
{
    /// <summary>One repository installed apps moved to, and which of them (#1018).</summary>
    public sealed record MovedHint(MovedTo To, string Domain, IReadOnlyList<string> AppNames);

    /// <summary>
    /// Apps that left the official catalog for their author's own repository (#1018, the official
    /// catalog's <c>moved_to</c>). The heads offer "this app now lives in &lt;repository&gt;: add it?"
    /// for apps on the TV whose new home is not among the user's sources yet; one click adds it with
    /// the key the signed official catalog vouches for, so there is no key question. From then on
    /// <see cref="InstalledAppUpdates"/> takes the app's updates from there.
    /// </summary>
    public static class MovedApps
    {
        /// <summary>The repositories to offer, given the matches of the apps on a TV against the catalog.</summary>
        public static IReadOnlyList<MovedHint> Hints(IEnumerable<CatalogMatch?> installedMatches, IEnumerable<UserSource> sources)
        {
            var have = new HashSet<string>(sources.Select(s => s.Url.Trim()), StringComparer.OrdinalIgnoreCase);
            return installedMatches
                .Where(m => m is { Release.Source.Kind: CatalogSourceKind.Official, Asset.MovedTo: not null })
                .Select(m => (m!.Release.Name, To: m.Asset.MovedTo!))
                .Where(x => !have.Contains(x.To.Url.Trim()))
                .GroupBy(x => x.To.Url.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new MovedHint(g.First().To, Domain(g.Key), g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();
        }

        /// <summary>
        /// Reads the repository a moved app went to and approves it only when its key is the one the
        /// official catalog named. Null source with the reason when it is not.
        /// </summary>
        public static async Task<(UserSource? Source, string? Problem)> AdoptAsync(SignedRepositories repositories, MovedTo to, CancellationToken ct = default)
        {
            var probe = await repositories.ProbeAsync(to.Url.Trim(), ct).ConfigureAwait(false);
            if (!probe.Ok)
                return (null, $"[{probe.Reason}] {probe.Message}");
            if (probe.Fingerprint != RepositoryVerifier.NormalizeFingerprint(to.Fingerprint))
                return (null, $"its key {probe.Fingerprint} is not the {RepositoryVerifier.NormalizeFingerprint(to.Fingerprint)} the official catalog names");
            return (probe.Approve(to.Url), null);
        }

        private static string Domain(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
    }
}
