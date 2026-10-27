using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Models
{
    public class GitHubRelease
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonPropertyName("published_at")]
        public string PublishedAt { get; set; } = string.Empty;

        // GitHub always sorts draft releases to the TOP of the /releases list, and drafts appear
        // there once the request is authenticated (token/gh). A draft's asset browser_download_url
        // 404s, so blindly taking releases[0] breaks whenever a release run leaves a draft behind.
        // Callers must skip drafts and take the newest published release.
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; set; } = new();

        [JsonIgnore]
        public string? PrimaryDownloadUrl => Assets?.FirstOrDefault()?.DownloadUrl;

        /// <summary>
        /// True when the source provider declared <c>cert_level: partner</c>, the installer then
        /// auto-requests Partner signing for this package. Stamped from the manifest, not serialized.
        /// </summary>
        [JsonIgnore]
        public bool RequiresPartner { get; set; }

        /// <summary>
        /// Category id (see <c>AppCategories</c>) the installer's filter groups this entry under.
        /// Stamped from the provider manifest or the community catalog, never from GitHub.
        /// </summary>
        [JsonIgnore]
        public string Category { get; set; } = "other";

        /// <summary>
        /// True when this entry folds several community files of the same app (forks, per-Tizen
        /// builds): its assets are then variants to choose from, not versions of one package.
        /// </summary>
        [JsonIgnore]
        public bool HasVariants { get; set; }

        /// <summary>
        /// True when the community catalog flags this app adult-only (#850); a grouped app is adult
        /// when any of its files is. Hidden from every list, count and search until the user opts
        /// in (<c>CommunityAppList.Visible</c>), and shown with an 18+ badge after that. Stamped
        /// from the catalog, never serialized: the snapshot carries the catalog itself.
        /// </summary>
        [JsonIgnore]
        public bool IsAdult { get; set; }

        /// <summary>
        /// True when the community catalog flags this app <c>root</c>: it gains root on the TV and
        /// runs a service from boot, and afterwards Host PC IP has to point at 127.0.0.1, which locks
        /// every PC out of SDB. The heads confirm before installing it and show a root badge; a
        /// grouped app is root when any of its files is. Stamped from the catalog, never serialized.
        /// </summary>
        [JsonIgnore]
        public bool IsRoot { get; set; }

        /// <summary>
        /// Download URL of the release's <c>catalog.json</c>, when the release ships one (the
        /// community bundle). Captured before the asset list is narrowed to .wgt/.tpk. Serialized
        /// (GitHub itself never sends a <c>catalog_url</c>) so the pre-resolved snapshot
        /// (<see cref="ResolvedCatalog"/>) carries it across.
        /// </summary>
        [JsonPropertyName("catalog_url")]
        public string? CatalogUrl { get; set; }

        /// <summary>
        /// The app's own repository page (https://github.com/owner/repo), shown on the app sheet as
        /// the way to its source, releases and licence (#820). Stamped by <c>SourceRepo</c> from the
        /// provider URL or the community catalog; null when neither names a repository.
        /// </summary>
        [JsonIgnore]
        public string? SourceUrl { get; set; }

        /// <summary>Who makes the app: the owner of <see cref="SourceUrl"/>'s repository.</summary>
        [JsonIgnore]
        public string? Author { get; set; }

        /// <summary>
        /// What the app is, in a sentence or two, shown on the app sheet above the category and
        /// source line (#835). Stamped from the community catalog's per-package description or the
        /// provider manifest's build info; empty when neither has one, and the sheet then shows
        /// only the info line.
        /// </summary>
        [JsonIgnore]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// All-time downloads of this app (<c>DownloadStatsService.Stamp</c>): the "Most downloaded"
        /// group's order and the count in its row. 0 when the counts could not be loaded.
        /// </summary>
        [JsonIgnore]
        public long Downloads { get; set; }

        public GitHubRelease()
        {
        }
    }

    public class Asset
    {
        [JsonPropertyName("name")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        /// <summary>
        /// The file's SHA-256 as <c>sha256:&lt;hex&gt;</c>: GitHub's releases API reports it per asset,
        /// and the snapshot tool computes it for an asset GitHub has none for. TV-Store installs a
        /// download only when its bytes match this hash from the signed catalog (#881); null in a
        /// live listing of an old release.
        /// </summary>
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonIgnore]
        public bool IsDefault => FileName.Equals("Jellyfin.wgt", StringComparison.OrdinalIgnoreCase);


        /// <summary>The file name, its manifest version when the catalog knows it, and the size.</summary>
        [JsonIgnore]
        public string DisplayText => string.IsNullOrWhiteSpace(PackageVersion)
            ? $"{FileName} ({FormatFileSize(Size)})"
            : $"{FileName} · v{PackageVersion} ({FormatFileSize(Size)})";

        /// <summary>
        /// The version in this file's manifest and the ids it declares (#820). The version is what the
        /// TV's app list shows once the file is installed; the app id is what an installed app is
        /// matched on. For the community bundle they come from its catalog.json and are stamped by
        /// <c>CommunityAppList</c>; for every other provider the snapshot tool reads them out of the
        /// asset itself and they travel in resolved.json. Null when neither has a record: a live
        /// GitHub API listing never carries them.
        /// </summary>
        [JsonPropertyName("package_version")]
        public string? PackageVersion { get; set; }
        [JsonPropertyName("app_id")]
        public string? AppId { get; set; }
        [JsonPropertyName("package_id")]
        public string? PackageId { get; set; }

        /// <summary>Label from the community catalog when this file is one variant of a grouped app.</summary>
        [JsonIgnore]
        public string? Variant { get; set; }

        /// <summary>
        /// What the user has to fill in before this file installs, from the community catalog
        /// (#791). Null for a file that needs nothing. Stamped by <c>CommunityAppList</c>, never
        /// serialized: the snapshot carries the catalog URL, and the catalog is read live.
        /// </summary>
        [JsonIgnore]
        public UserSettingsSpec? UserSettings { get; set; }

        /// <summary>The variant label, or the bare file name when the file is not part of a group.</summary>
        [JsonIgnore]
        public string VariantLabel => string.IsNullOrWhiteSpace(Variant)
            ? System.IO.Path.GetFileNameWithoutExtension(FileName)
            : Variant;

        /// <summary>What the desktop's version/variant dropdown shows: the variant label, then the file, when there is one.</summary>
        [JsonIgnore]
        public string LongDisplayText => string.IsNullOrWhiteSpace(Variant)
            ? DisplayText
            : $"{Variant}: {DisplayText}";

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            int order = 0;
            double len = bytes;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        public Asset()
        {
        }
    }
}
