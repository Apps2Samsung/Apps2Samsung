using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The Tizen version a package asks for (<see cref="Asset.RequiredVersion"/>, from the catalog)
    /// held against the one a TV runs, the way TV-Store does it (TV-Store#47): a build the TV is too
    /// old for gets a "Needs Tizen X" badge and no Install button, instead of the TV refusing it with
    /// install failed[118, -19]. Lenient throughout: an unknown version on either side is no refusal,
    /// the pre-install gate and the TV itself still have the last word.
    /// </summary>
    public static class TizenRequirement
    {
        /// <summary>True when the TV cannot run the asset: both versions known and the TV's is older.</summary>
        public static bool NeedsNewer(Asset asset, Version? tvVersion) =>
            WgtManifest.RequiresNewerTizen(tvVersion, asset.RequiredVersion);

        /// <summary>The lowest Tizen version any of the assets asks for, as the catalog wrote it ("6.0"); null when none says.</summary>
        public static string? Lowest(IEnumerable<Asset> assets)
        {
            string? lowest = null;
            Version? lowestParsed = null;
            foreach (var asset in assets)
            {
                if (!Version.TryParse(asset.RequiredVersion, out var parsed))
                    continue;
                if (lowestParsed is null || parsed < lowestParsed)
                {
                    lowest = asset.RequiredVersion;
                    lowestParsed = parsed;
                }
            }
            return lowest;
        }

        /// <summary>True when the TV is known and no build of the release runs on it.</summary>
        public static bool NoneFits(GitHubRelease release, Version? tvVersion) =>
            tvVersion is not null && release.Assets.Count > 0 && release.Assets.All(a => NeedsNewer(a, tvVersion));

        /// <summary>
        /// The build to land on when the sheet opens: <paramref name="preferred"/> when the TV runs it
        /// (or the TV is unknown), else the first build that fits, else <paramref name="preferred"/>.
        /// </summary>
        public static Asset? FirstFitting(IEnumerable<Asset> assets, Asset? preferred, Version? tvVersion)
        {
            if (preferred is null || !NeedsNewer(preferred, tvVersion))
                return preferred;
            return assets.FirstOrDefault(a => !NeedsNewer(a, tvVersion)) ?? preferred;
        }
    }
}
