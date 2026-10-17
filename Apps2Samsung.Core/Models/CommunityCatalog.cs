using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// The <c>catalog.json</c> asset the tizen-community-packages release ships next to its
    /// .wgt/.tpk files: one entry per file, carrying the package's display name, category and
    /// (for forks and per-Tizen builds of the same app) the group it folds into. Built by
    /// <c>scripts/build-catalog.sh</c> in that repo; a release without it (older bundles) simply
    /// yields the flat, uncategorised list the installer showed before.
    /// </summary>
    public sealed class CommunityCatalog
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Category id → English display name, the fallback when a head has no translation for an id.</summary>
        public Dictionary<string, string> Categories { get; set; } = new();

        public List<CommunityCatalogEntry> Apps { get; set; } = new();
    }

    public sealed class CommunityCatalogEntry
    {
        /// <summary>The file name inside the bundle (the release asset name), the key the installer matches on.</summary>
        public string File { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>One of the ids in <see cref="AppCategories"/>; anything unknown lands in "other".</summary>
        public string Category { get; set; } = "";
        /// <summary>When set, every file with the same group becomes one app entry with a variant picker.</summary>
        public string? Group { get; set; }
        /// <summary>Label of this file inside its group ("OneLiberty · Chrome", "Tizen 8").</summary>
        public string? Variant { get; set; }
        public string Repo { get; set; } = "";
        public string Host { get; set; } = "github";
    }
}
