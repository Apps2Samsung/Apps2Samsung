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

        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; set; } = new();

        [JsonIgnore]
        public string? PrimaryDownloadUrl => Assets?.FirstOrDefault()?.DownloadUrl;

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
