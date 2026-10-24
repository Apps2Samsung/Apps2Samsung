using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// One package installed on one TV under an id other than its own (#839). Kept so every later
    /// install of that package on that TV (an update, a reinstall) lands on the same tile, and so
    /// the installed-apps view can tell which catalog entry a renamed app came from.
    /// </summary>
    public sealed class PackageIdRenameRecord
    {
        /// <summary>The TV the rename applies to; the same package keeps its own id on every other TV.</summary>
        [JsonPropertyName("tvDuid")]
        public string TvDuid { get; set; } = "";
        /// <summary>The package id the file was built with.</summary>
        [JsonPropertyName("originalPackageId")]
        public string OriginalPackageId { get; set; } = "";
        /// <summary>The id it was installed under.</summary>
        [JsonPropertyName("newPackageId")]
        public string NewPackageId { get; set; } = "";
        /// <summary>The application id the file was built with ("Pkg.App"), what the catalog records as <c>app_id</c>.</summary>
        [JsonPropertyName("originalAppId")]
        public string? OriginalAppId { get; set; }
        /// <summary>The application id the TV lists it under after the rename ("New.App").</summary>
        [JsonPropertyName("newAppId")]
        public string? NewAppId { get; set; }
        /// <summary>The package file name, for the record.</summary>
        [JsonPropertyName("packageFile")]
        public string? PackageFile { get; set; }
        [JsonPropertyName("renamedAtUtc")]
        public DateTimeOffset RenamedAtUtc { get; set; }
    }

    /// <summary>
    /// The rename records as the heads store them in one JSON string (<c>IAppConfig.PackageIdRenamesJson</c>):
    /// the desktop's settings.json, the phone's Preferences, the CLI's data folder.
    /// </summary>
    public static class PackageIdRenameStore
    {
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public static List<PackageIdRenameRecord> Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<PackageIdRenameRecord>();
            try
            {
                return JsonSerializer.Deserialize<List<PackageIdRenameRecord>>(json, Json) ?? new List<PackageIdRenameRecord>();
            }
            catch (JsonException)
            {
                return new List<PackageIdRenameRecord>();
            }
        }

        public static string Write(List<PackageIdRenameRecord> records) =>
            records.Count == 0 ? string.Empty : JsonSerializer.Serialize(records, Json);

        /// <summary>The rename to apply when <paramref name="originalPackageId"/> is installed on <paramref name="tvDuid"/>, or null.</summary>
        public static PackageIdRenameRecord? Find(string? json, string? tvDuid, string? originalPackageId)
        {
            if (string.IsNullOrWhiteSpace(tvDuid) || string.IsNullOrWhiteSpace(originalPackageId))
                return null;
            return Read(json).LastOrDefault(r =>
                string.Equals(r.TvDuid, tvDuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.OriginalPackageId, originalPackageId, StringComparison.Ordinal));
        }

        /// <summary>Adds or replaces the record for (TV, original package id).</summary>
        public static string Set(string? json, PackageIdRenameRecord record)
        {
            var records = Read(json);
            records.RemoveAll(r =>
                string.Equals(r.TvDuid, record.TvDuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.OriginalPackageId, record.OriginalPackageId, StringComparison.Ordinal));
            records.Add(record);
            return Write(records);
        }

        /// <summary>Forgets the rename for (TV, original package id); the next install uses the package's own id again.</summary>
        public static string Remove(string? json, string? tvDuid, string? originalPackageId)
        {
            var records = Read(json);
            var n = records.RemoveAll(r =>
                string.Equals(r.TvDuid, tvDuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.OriginalPackageId, originalPackageId, StringComparison.Ordinal));
            return n == 0 ? json ?? string.Empty : Write(records);
        }

        /// <summary>
        /// The application id a package was built with, given the id the TV lists it under, or null
        /// when <paramref name="installedAppId"/> is not a renamed app. Not keyed by TV: a renamed id
        /// is random, so a hit is a hit, and the installed-apps views have no DUID at hand.
        /// </summary>
        public static string? OriginalAppIdOf(string? json, string? installedAppId)
        {
            if (string.IsNullOrWhiteSpace(installedAppId))
                return null;
            var record = Read(json).LastOrDefault(r =>
                !string.IsNullOrEmpty(r.NewAppId) && string.Equals(r.NewAppId, installedAppId, StringComparison.OrdinalIgnoreCase));
            if (record is not null)
                return record.OriginalAppId;

            // A record written before the app ids were known: match on the package part of the id.
            var dot = installedAppId.IndexOf('.');
            var installedPackage = dot > 0 ? installedAppId[..dot] : installedAppId;
            record = Read(json).LastOrDefault(r => string.Equals(r.NewPackageId, installedPackage, StringComparison.OrdinalIgnoreCase));
            return record is null ? null : dot > 0 ? record.OriginalPackageId + installedAppId[dot..] : record.OriginalPackageId;
        }

        /// <summary>The installed application id for a catalog app id on <paramref name="tvDuid"/>, when it was renamed there; else the id itself.</summary>
        public static string InstalledAppIdOf(string? json, string? tvDuid, string appId)
        {
            var dot = appId.IndexOf('.');
            var package = dot > 0 ? appId[..dot] : appId;
            var record = Find(json, tvDuid, package);
            if (record is null)
                return appId;
            return !string.IsNullOrEmpty(record.NewAppId) && string.Equals(record.OriginalAppId, appId, StringComparison.OrdinalIgnoreCase)
                ? record.NewAppId!
                : dot > 0 ? record.NewPackageId + appId[dot..] : record.NewPackageId;
        }
    }
}
