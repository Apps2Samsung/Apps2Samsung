using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Which file of a catalog app one TV got (#975). An app with several variants (Litefin's
    /// Normal, Legacy, Modern, Ultra builds) ships them all under one application id and one
    /// version, so the TV's app list cannot say which of them it runs. The head that installed it
    /// can, so it writes this down, and an update then takes the same variant instead of the app's
    /// default file.
    /// </summary>
    public sealed class InstalledVariantRecord
    {
        /// <summary>The TV's address at install time; what the installed-apps views have at hand.</summary>
        [JsonPropertyName("tvIp")]
        public string TvIp { get; set; } = "";
        /// <summary>The TV's DUID when the install read it; the surer key when two TVs swap addresses.</summary>
        [JsonPropertyName("tvDuid")]
        public string? TvDuid { get; set; }
        /// <summary>The application id the file declares, as the catalog records it (<c>app_id</c>).</summary>
        [JsonPropertyName("appId")]
        public string AppId { get; set; } = "";
        /// <summary>The catalog app the file belongs to, as listed.</summary>
        [JsonPropertyName("catalogApp")]
        public string? CatalogApp { get; set; }
        /// <summary>The variant label the catalog gives the file, when the app has variants.</summary>
        [JsonPropertyName("variant")]
        public string? Variant { get; set; }
        /// <summary>The file installed, with its version in the name.</summary>
        [JsonPropertyName("packageFile")]
        public string PackageFile { get; set; } = "";
        [JsonPropertyName("installedAtUtc")]
        public DateTimeOffset InstalledAtUtc { get; set; }
        /// <summary>
        /// The source the file came from (#1018), as <see cref="Models.CatalogSource.Key"/>: "official",
        /// a signed repository's id, or "unverified:&lt;name&gt;". Updates come only from this source.
        /// Null on a record written before sources were recorded: treated as not from a repository.
        /// </summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }

    /// <summary>
    /// The installed-variant records as the heads keep them in one JSON string: the desktop's
    /// settings.json (shared with the CLI), the phone's Preferences. One record per TV and app id;
    /// a later install replaces the earlier one.
    /// </summary>
    public static class InstalledVariantStore
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        // A version in a file name: 2.0.30, v1.2, 1.0.0-beta.3. Taken out to compare two releases'
        // files of the same variant when the catalog gives them no variant label. Only a known
        // pre-release word may follow the numbers: "-Tizen-Normal" after "2.0.30" is the variant.
        private static readonly Regex VersionInName = new(
            @"[-_.]?v?\d+(\.\d+)+(?:[-.](?:alpha|beta|rc|pre|preview|dev|nightly)(?:[.-]?\d+)*)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static List<InstalledVariantRecord> Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<InstalledVariantRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<InstalledVariantRecord>>(json, Json) ?? new List<InstalledVariantRecord>();
            }
            catch (JsonException)
            {
                return new List<InstalledVariantRecord>();
            }
        }

        public static string Write(List<InstalledVariantRecord> records) =>
            records.Count == 0 ? string.Empty : JsonSerializer.Serialize(records, Json);

        /// <summary>Adds or replaces the record for (TV, app id). A record without an app id or a file is not kept.</summary>
        public static string Set(string? json, InstalledVariantRecord record)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.AppId) || string.IsNullOrWhiteSpace(record.PackageFile))
                return json ?? string.Empty;
            var records = Read(json);
            records.RemoveAll(r => SameTv(r, record.TvIp, record.TvDuid) && SameApp(r, record.AppId));
            records.Add(record);
            return Write(records);
        }

        /// <summary>
        /// The record for <paramref name="appId"/> on the TV at <paramref name="tvIp"/> (or with
        /// <paramref name="tvDuid"/>, when known), or null. With no record for that TV, the one
        /// every other TV agrees on: a household with one TV that changed address keeps its variant.
        /// </summary>
        public static InstalledVariantRecord? Find(string? json, string? tvIp, string? appId, string? tvDuid = null)
        {
            if (string.IsNullOrWhiteSpace(appId))
                return null;
            var records = Read(json).Where(r => SameApp(r, appId)).ToList();
            if (records.Count == 0)
                return null;

            var onThisTv = records.LastOrDefault(r => SameTv(r, tvIp, tvDuid));
            if (onThisTv is not null)
                return onThisTv;

            var keys = records.Select(VariantKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return keys.Count == 1 ? records[^1] : null;
        }

        /// <summary>
        /// Whether <paramref name="fileName"/> is the same variant as the recorded file: the same
        /// label when the catalog gives one, else the same name once the version is taken out
        /// (<c>Litefin-2.0.30-Tizen-Normal.wgt</c> and <c>Litefin-2.0.31-Tizen-Normal.wgt</c>).
        /// </summary>
        public static bool IsSameVariant(InstalledVariantRecord record, string? fileName, string? variant)
        {
            if (record is null || string.IsNullOrWhiteSpace(fileName))
                return false;
            if (!string.IsNullOrWhiteSpace(record.Variant) && !string.IsNullOrWhiteSpace(variant))
                return string.Equals(record.Variant, variant, StringComparison.OrdinalIgnoreCase);
            return string.Equals(Family(record.PackageFile), Family(fileName), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The file name with its version taken out, lower-cased: what stays the same from one release to the next.</summary>
        public static string Family(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;
            var name = fileName.Trim();
            var dot = name.LastIndexOf('.');
            var stem = dot > 0 ? name[..dot] : name;
            var ext = dot > 0 ? name[dot..] : string.Empty;
            return (VersionInName.Replace(stem, string.Empty) + ext).ToLowerInvariant();
        }

        private static string VariantKey(InstalledVariantRecord r) =>
            string.IsNullOrWhiteSpace(r.Variant) ? Family(r.PackageFile) : r.Variant.Trim().ToLowerInvariant();

        private static bool SameApp(InstalledVariantRecord r, string appId) =>
            string.Equals(r.AppId, appId, StringComparison.OrdinalIgnoreCase);

        private static bool SameTv(InstalledVariantRecord r, string? tvIp, string? tvDuid)
        {
            if (!string.IsNullOrWhiteSpace(tvDuid) && !string.IsNullOrWhiteSpace(r.TvDuid))
                return string.Equals(r.TvDuid, tvDuid, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrWhiteSpace(tvIp) && string.Equals(r.TvIp, tvIp, StringComparison.OrdinalIgnoreCase);
        }
    }
}
