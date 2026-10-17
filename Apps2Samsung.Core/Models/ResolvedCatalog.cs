using System;
using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// The pre-resolved app catalog apps2samsung.com serves at <c>/catalog/resolved.json</c>: every
    /// provider of <c>third-party-apps.json</c> already fetched from the GitHub API, server-side, with
    /// an authenticated token (<c>.github/workflows/catalog-snapshot.yml</c> → <c>tools/CatalogSnapshot</c>).
    /// A client that reads this file needs no <c>api.github.com</c> call at all, which is what keeps
    /// users behind a shared NAT / VPN exit from burning through the 60 req/h unauthenticated limit.
    /// Built with the same Core code the heads run (<c>AddLatestRelease</c>, <c>BuildInfoService</c>),
    /// so the shapes below are the ones the heads already deserialize.
    /// </summary>
    public sealed class ResolvedCatalog
    {
        /// <summary>The schema a client must understand to use the file; see <see cref="CurrentSchemaVersion"/>.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTimeOffset GeneratedAt { get; set; }
        /// <summary>The <c>schemaVersion</c> of the manifest the snapshot was resolved from.</summary>
        public int ManifestSchemaVersion { get; set; }
        public List<ResolvedProvider> Providers { get; set; } = new();
    }

    public sealed class ResolvedProvider
    {
        /// <summary>The manifest provider's <c>id</c>, when it has one.</summary>
        public string Id { get; set; } = "";
        /// <summary>The manifest provider's releases URL; the fallback key when there is no id.</summary>
        public string Url { get; set; } = "";
        /// <summary>
        /// False when the generator's own fetch of this provider failed and <see cref="Releases"/>
        /// is the last good data carried over from the previous snapshot (or empty). A client treats
        /// such a provider as absent and takes its own live path.
        /// </summary>
        public bool Ok { get; set; }
        /// <summary>Latest release tag, as <c>BuildInfoService.GetLatestReleaseTagAsync</c> reports it.</summary>
        public string LatestTag { get; set; } = "";
        /// <summary>
        /// The provider's releases with the manifest's full <c>take</c>, assets already narrowed to
        /// .wgt/.tpk and the display name / prefix applied — exactly what <c>GetReleasesWithStatusAsync</c>
        /// returns. The client still trims to its own <c>take</c>.
        /// </summary>
        public List<GitHubRelease> Releases { get; set; } = new();
        /// <summary>The community bundle's <c>catalog.json</c>, inlined when the latest release ships one.</summary>
        public CommunityCatalog? Catalog { get; set; }
    }
}
