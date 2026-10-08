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
        /// True when the source provider declared <c>cert_level: partner</c> — the installer then
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
        /// Download URL of the release's <c>catalog.json</c>, when the release ships one (the
        /// community bundle). Captured before the asset list is narrowed to .wgt/.tpk.
        /// </summary>
        [JsonIgnore]
        public string? CatalogUrl { get; set; }

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

        [JsonIgnore]
        public bool IsDefault => FileName.Equals("Jellyfin.wgt", StringComparison.OrdinalIgnoreCase);


        [JsonIgnore]
        public string DisplayText => $"{FileName} ({FormatFileSize(Size)})";

        /// <summary>Label from the community catalog when this file is one variant of a grouped app.</summary>
        [JsonIgnore]
        public string? Variant { get; set; }

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
