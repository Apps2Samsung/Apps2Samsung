using System;
using System.IO;
using System.IO.Compression;

namespace Apps2Samsung.Backup
{
    /// <summary>What <see cref="BackupService.Import"/> pulled out of a backup archive.</summary>
    /// <param name="SettingsJson">The raw <c>settings.json</c> content (desktop AppSettings schema), or
    /// null if the archive had none. Each head decides how to apply it (desktop merges into its
    /// settings.json; mobile maps the known keys into Preferences).</param>
    /// <param name="CertificateFilesRestored">How many certificate files were written back.</param>
    public sealed record BackupImportResult(string? SettingsJson, int CertificateFilesRestored);

    /// <summary>
    /// Head-agnostic settings + certificate backup. Produces/consumes a single .zip so a config can move
    /// PC ↔ Mac ↔ mobile (#510). The archive holds:
    ///   • <c>settings.json</c>  — the settings blob (desktop AppSettings JSON schema; the shared format
    ///                             both heads read/write, so keys line up across platforms).
    ///   • <c>Certificate/…</c>  — the generated signing profiles (the folder each head exposes as
    ///                             <see cref="Apps2Samsung.Configuration.IAppConfig.CertificateStorePath"/>),
    ///                             so the Samsung login + already-installed apps' signing survive the move.
    ///   • <c>saved-tvs.json</c>: the app list of each TV (#1069, <see cref="Apps2Samsung.Catalog.SavedTvs"/>),
    ///                             so a TV can be restored from the other device too. No typed values in it.
    /// Both heads store certs under the same relative <c>Certificate/</c> layout, so that half is uniform;
    /// only the settings mapping is head-specific and lives in each head.
    /// </summary>
    public static class BackupService
    {
        public const string SettingsEntryName = "settings.json";
        private const string CertPrefix = "Certificate/";
        public const string SavedTvsEntryName = Apps2Samsung.Catalog.SavedTvs.FileName;

        /// <summary>Write <paramref name="settingsJson"/> and the whole <paramref name="certificateStorePath"/>
        /// tree into <paramref name="zipOutput"/>. The stream is left open for the caller to dispose.</summary>
        public static void Export(Stream zipOutput, string? settingsJson, string certificateStorePath)
        {
            using var zip = new ZipArchive(zipOutput, ZipArchiveMode.Create, leaveOpen: true);

            if (!string.IsNullOrEmpty(settingsJson))
            {
                var entry = zip.CreateEntry(SettingsEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(settingsJson);
            }

            // The saved app lists (#1069), read from the store the head configured; nothing when it has none.
            if (Apps2Samsung.Catalog.SavedTvs.ExportJson() is { } savedTvs)
            {
                var entry = zip.CreateEntry(SavedTvsEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(savedTvs);
            }

            if (Directory.Exists(certificateStorePath))
            {
                foreach (var file in Directory.EnumerateFiles(certificateStorePath, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(certificateStorePath, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, CertPrefix + rel, CompressionLevel.Optimal);
                }
            }
        }

        /// <summary>Restore certificates into <paramref name="certificateStorePath"/> (overwriting) and
        /// return the archive's settings.json for the caller to apply.</summary>
        public static BackupImportResult Import(Stream zipInput, string certificateStorePath)
        {
            string? settingsJson = null;
            int certFiles = 0;

            using var zip = new ZipArchive(zipInput, ZipArchiveMode.Read);
            var certRoot = Path.GetFullPath(certificateStorePath);

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Equals(SettingsEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    settingsJson = reader.ReadToEnd();
                }
                else if (entry.FullName.Equals(SavedTvsEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    // Added to what this device has; a list it read more recently itself is kept.
                    using var reader = new StreamReader(entry.Open());
                    Apps2Samsung.Catalog.SavedTvs.Merge(reader.ReadToEnd());
                }
                else if (entry.FullName.StartsWith(CertPrefix, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrEmpty(entry.Name))
                {
                    var rel = entry.FullName.Substring(CertPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    var dest = Path.GetFullPath(Path.Combine(certificateStorePath, rel));

                    // Zip-slip guard: never write outside the certificate store.
                    if (!dest.StartsWith(certRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !dest.Equals(certRoot, StringComparison.Ordinal))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    certFiles++;
                }
            }

            if (certFiles > 0)
                Apps2Samsung.Certificate.CertificateBackups.Changed(); // #1068
            return new BackupImportResult(settingsJson, certFiles);
        }
    }
}
