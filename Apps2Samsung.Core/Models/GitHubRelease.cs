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
