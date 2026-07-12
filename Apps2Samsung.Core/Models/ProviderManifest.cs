using System.Collections.Generic;

namespace Apps2Samsung.Models
{
    public sealed class ProviderManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public PreviewImageDefaults PreviewImages { get; set; } = new();
        public List<ProviderEntry> Providers { get; set; } = new();
        public List<CommunityAppEntry> CommunityApps { get; set; } = new();
    }

    public sealed class PreviewImageDefaults
    {
        public string Jellyfin { get; set; } = "";
    }

    public sealed class ProviderEntry
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Take { get; set; } = 1;
        /// <summary>
        /// When true, every .wgt asset of the fetched release becomes its own
        /// entry in the release list (used for the community package bundle).
        /// </summary>
        public bool ExpandAssets { get; set; }
        public ProviderBuildInfo? BuildInfo { get; set; }
    }

    public sealed class ProviderBuildInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string? PreviewImage { get; set; }
    }

    public sealed class CommunityAppEntry
    {
        public string MatchName { get; set; } = "";
        public string PreviewImage { get; set; } = "";
    }
}
