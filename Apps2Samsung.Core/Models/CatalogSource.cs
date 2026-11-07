namespace Apps2Samsung.Models
{
    /// <summary>Where a catalog entry comes from (#1018), as every head labels it.</summary>
    public enum CatalogSourceKind
    {
        /// <summary>The official catalog: third-party-apps.json and the community packages.</summary>
        Official,
        /// <summary>An independent signed repository the user added and approved the key of.</summary>
        Repository,
        /// <summary>A source the user added that is not signed: a GitHub repository, a release, a file link or a feed.</summary>
        Unverified,
    }

    /// <summary>
    /// The provenance of a catalog entry (#1018): which source it is from, for the label in the list
    /// and the details, for matching an installed app to the source it came from, and whether it may
    /// be installed at all (a repository that failed its last check is listed from its last verified
    /// copy, but nothing is installed from it).
    /// </summary>
    /// <param name="Url">A repository's manifest link, which the official catalog's <c>moved_to</c> names.</param>
    public sealed record CatalogSource(CatalogSourceKind Kind, string Name, string? RepositoryId = null, string? InstallBlockedReason = null, string? Url = null)
    {
        public static readonly CatalogSource Official = new(CatalogSourceKind.Official, "Official");

        /// <summary>A key for "the same source" across runs: "official", the repository id, or "unverified:&lt;name&gt;".</summary>
        public string Key => Kind switch
        {
            CatalogSourceKind.Official => "official",
            CatalogSourceKind.Repository => RepositoryId ?? Name,
            _ => "unverified:" + Name,
        };

        public bool CanInstall => InstallBlockedReason is null;
    }
}
