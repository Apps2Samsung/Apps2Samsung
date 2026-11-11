using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apps2Samsung.Helpers.Core;

namespace Apps2Samsung.Certificate
{
    /// <summary>Where the automatic certificate backups go. A folder on a computer, Downloads on Android.</summary>
    public interface ICertificateBackupTarget
    {
        /// <summary>The place in words for Settings, e.g. "Documents\Apps2Samsung\Certificate backups".</summary>
        string DisplayLocation { get; }

        /// <summary>Writes one backup and returns where it went (a path, or a content uri), the key for <see cref="Exists"/> and <see cref="Delete"/>.</summary>
        string Write(string fileName, byte[] content);

        /// <summary>False when the user deleted or moved the file, so the profiles in it count as not backed up.</summary>
        bool Exists(string location);

        /// <summary>Removes an old backup. Best effort: a target that cannot delete leaves it.</summary>
        void Delete(string location);
    }

    /// <summary>A plain folder: desktop, the CLI, iOS's Documents.</summary>
    public sealed class FolderBackupTarget : ICertificateBackupTarget
    {
        private readonly string _folder;

        public FolderBackupTarget(string folder, string? displayLocation = null)
        {
            _folder = folder;
            DisplayLocation = displayLocation ?? folder;
        }

        public string Folder => _folder;
        public string DisplayLocation { get; }

        public string Write(string fileName, byte[] content)
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, fileName);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
            return path;
        }

        public bool Exists(string location) => File.Exists(location);

        public void Delete(string location)
        {
            try { File.Delete(location); } catch { /* best effort */ }
        }
    }

    /// <summary>One profile as it was saved in a backup.</summary>
    /// <param name="ProfileName">The profile folder, e.g. "Jelly2Sams - Public".</param>
    /// <param name="Fingerprint">SHA-256 over the author, the distributor and the password: the same value means the same profile.</param>
    /// <param name="AuthorHash">SHA-256 of the author certificate alone: the part a TV binds an app to.</param>
    public sealed record CertificateBackupProfile(string ProfileName, string Fingerprint, string AuthorHash);

    /// <summary>One backup file the app wrote.</summary>
    public sealed record CertificateBackupEntry(string FileName, string Location, DateTime CreatedUtc, IReadOnlyList<CertificateBackupProfile> Profiles);

    /// <summary>A profile on this device and the newest backup that holds it exactly as it is now.</summary>
    public sealed record CertificateBackupProfileStatus(CertificateArchiveProfile Profile, DateTime? BackedUpUtc)
    {
        public bool IsBackedUp => BackedUpUtc is not null;
    }

    /// <summary>What Settings › Backup shows.</summary>
    public sealed record CertificateBackupStatus(
        IReadOnlyList<CertificateBackupProfileStatus> Profiles,
        CertificateBackupEntry? Latest,
        string DisplayLocation,
        string? LastError)
    {
        public IEnumerable<CertificateBackupProfileStatus> NotBackedUp => Profiles.Where(p => !p.IsBackedUp);
        public bool AllBackedUp => Profiles.All(p => p.IsBackedUp);
    }

    /// <summary>
    /// Automatic certificate backups (#1068). A lost author certificate means every app it signed has to
    /// be removed from the TV before it can be updated ([118, -22]); the manual export (#510, #921) only
    /// helps someone who thought of it. Each head points this at a place outside its own data folder
    /// (<see cref="Configure"/>); after that, every new, renewed or imported certificate is saved there
    /// by itself (<see cref="Changed"/>), and Settings shows which profiles are in a backup.
    /// <para>
    /// The heads keep an index of what they wrote (<see cref="IndexFileName"/>) rather than reading the
    /// backups back, so a target that can write but not list (Android's Downloads) works the same. A
    /// backup always holds every usable profile, and old ones are pruned to <see cref="Keep"/>, except a
    /// backup that is the last one holding an author certificate: that is exactly the file someone
    /// needs when the TV refuses an update.
    /// </para>
    /// </summary>
    public static class CertificateBackups
    {
        public const int Keep = 5;
        public const string FilePrefix = "a2s-certificates-";
        public const string IndexFileName = "certificate-backups.json";

        private static readonly object Gate = new();
        private static string? _storePath;
        private static string? _indexPath;
        private static ICertificateBackupTarget? _target;
        private static string? _lastError;

        /// <summary>Raised after a backup was written, or a write failed, so an open Settings page can refresh.</summary>
        public static event Action? StatusChanged;

        public static bool IsConfigured => _target is not null;

        /// <summary>Called once at startup by each head.</summary>
        public static void Configure(string storePath, string indexPath, ICertificateBackupTarget target)
        {
            lock (Gate)
            {
                _storePath = storePath;
                _indexPath = indexPath;
                _target = target;
                _lastError = null;
            }
        }

        /// <summary>For tests: back to "no head configured".</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _storePath = _indexPath = null;
                _target = null;
                _lastError = null;
            }
        }

        /// <summary>
        /// The desktop and CLI folder: Documents\Apps2Samsung\Certificate backups, or the data folder when
        /// the system has no Documents (a server, Termux).
        /// </summary>
        public static string DefaultFolder(string dataDir)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)
                ? Path.Combine(dataDir, "Certificate backups")
                : Path.Combine(documents, "Apps2Samsung", "Certificate backups");
        }

        /// <summary>
        /// A certificate was made, renewed or imported: back it up when it is not in a backup yet. Never
        /// throws; a failure is logged and shown in Settings.
        /// </summary>
        public static void Changed()
        {
            if (!IsConfigured)
                return;
            try
            {
                BackUp(force: false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CertBackup] Automatic backup failed: {ex.Message}");
            }
        }

        /// <summary>The Back up now button: a backup even when everything is already in one. Throws on failure.</summary>
        public static CertificateBackupEntry? BackUpNow() => BackUp(force: true);

        public static CertificateBackupStatus? Status()
        {
            lock (Gate)
            {
                if (_target is null || _storePath is null || _indexPath is null)
                    return null;
                return Status(_storePath, _indexPath, _target, _lastError);
            }
        }

        /// <summary>
        /// The newest backup as a path for the certificate sheet's Save step, when every certificate is
        /// in a backup; null otherwise (not configured, nothing yet, or the automatic one failed).
        /// </summary>
        public static string? LatestBackupDisplayPath()
        {
            try
            {
                return Status() is { AllBackedUp: true, Latest: { } latest } status
                    ? Path.Combine(status.DisplayLocation, latest.FileName)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The backups that hold <paramref name="profileName"/> with another author certificate than the
        /// one on this device now, newest first: the candidates when a TV refuses an update because the
        /// app was signed with an older certificate.
        /// </summary>
        public static IReadOnlyList<CertificateBackupEntry> OtherAuthors(string profileName)
        {
            lock (Gate)
            {
                if (_target is null || _storePath is null || _indexPath is null)
                    return Array.Empty<CertificateBackupEntry>();
                var current = Fingerprints(Path.Combine(_storePath, profileName))?.AuthorHash;
                return LoadIndex(_indexPath, _target)
                    .Where(e => e.Profiles.Any(p => p.ProfileName == profileName && p.AuthorHash != current))
                    .OrderByDescending(e => e.CreatedUtc)
                    .ToList();
            }
        }

        private static CertificateBackupEntry? BackUp(bool force)
        {
            CertificateBackupEntry? written;
            lock (Gate)
            {
                if (_target is null || _storePath is null || _indexPath is null)
                    return null;
                try
                {
                    written = BackUp(_storePath, _indexPath, _target, DateTime.UtcNow, force);
                    _lastError = null;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    StatusChanged?.Invoke();
                    throw;
                }
            }

            if (written is not null)
            {
                Trace.WriteLine($"[CertBackup] Saved {string.Join(", ", written.Profiles.Select(p => p.ProfileName))} to {written.Location}");
                StatusChanged?.Invoke();
            }
            return written;
        }

        // ---- The parts with no global state, which the tests drive ----

        /// <summary>
        /// Writes a backup of every usable profile when one of them is in no backup (or <paramref name="force"/>),
        /// records it in the index and prunes. Null when there was nothing to do.
        /// </summary>
        public static CertificateBackupEntry? BackUp(string storePath, string indexPath, ICertificateBackupTarget target, DateTime nowUtc, bool force)
        {
            var profiles = CertificateArchive.UsableProfiles(storePath);
            if (profiles.Count == 0)
                return null;

            var index = LoadIndex(indexPath, target);
            var current = profiles
                .Select(p => Fingerprints(p.Directory))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
            if (!force && current.All(c => index.Any(e => e.Profiles.Any(p => p.ProfileName == c.ProfileName && p.Fingerprint == c.Fingerprint))))
                return null;

            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                CertificateArchive.ExportAll(buffer, storePath);
                bytes = buffer.ToArray();
            }

            var stem = FilePrefix + nowUtc.ToLocalTime().ToString("yyyy-MM-dd-HHmmss");
            var fileName = stem + CertificateArchive.FileExtension;
            for (var n = 2; index.Any(e => e.FileName == fileName); n++)
                fileName = $"{stem}-{n}{CertificateArchive.FileExtension}";
            var location = target.Write(fileName, bytes);
            var entry = new CertificateBackupEntry(fileName, location, nowUtc, current);

            index.RemoveAll(e => e.Location == location);
            index.Add(entry);
            index = Prune(index, target);
            SaveIndex(indexPath, index);
            return entry;
        }

        public static CertificateBackupStatus Status(string storePath, string indexPath, ICertificateBackupTarget target, string? lastError = null)
        {
            var index = LoadIndex(indexPath, target);
            var rows = new List<CertificateBackupProfileStatus>();
            foreach (var (dir, profile) in CertificateArchive.UsableProfiles(storePath))
            {
                var now = Fingerprints(dir);
                var newest = now is null ? null : index
                    .Where(e => e.Profiles.Any(p => p.ProfileName == now.ProfileName && p.Fingerprint == now.Fingerprint))
                    .OrderByDescending(e => e.CreatedUtc)
                    .FirstOrDefault();
                rows.Add(new CertificateBackupProfileStatus(profile, newest?.CreatedUtc));
            }

            var latest = index.OrderByDescending(e => e.CreatedUtc).FirstOrDefault();
            return new CertificateBackupStatus(rows, latest, target.DisplayLocation, lastError);
        }

        // Newest Keep stay; an older one goes only when every author certificate in it is also in a kept one.
        private static List<CertificateBackupEntry> Prune(List<CertificateBackupEntry> index, ICertificateBackupTarget target)
        {
            var ordered = index.OrderByDescending(e => e.CreatedUtc).ToList();
            var kept = ordered.Take(Keep).ToList();
            foreach (var old in ordered.Skip(Keep))
            {
                var covered = old.Profiles.All(p => kept.Any(k => k.Profiles.Any(q => q.ProfileName == p.ProfileName && q.AuthorHash == p.AuthorHash)));
                if (covered)
                    target.Delete(old.Location);
                else
                    kept.Add(old);
            }
            return kept;
        }

        /// <summary>The two hashes of a profile folder, or null when it is not a complete profile.</summary>
        public static CertificateBackupProfile? Fingerprints(string profileDir)
        {
            var author = Path.Combine(profileDir, Constants.Certificate.AuthorFileName);
            var distributor = Path.Combine(profileDir, Constants.Certificate.DistributorFileName);
            var password = Path.Combine(profileDir, Constants.Certificate.PasswordFileName);
            if (!File.Exists(author) || !File.Exists(distributor) || !File.Exists(password))
                return null;

            var authorBytes = File.ReadAllBytes(author);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(authorBytes);
            sha.AppendData(new byte[] { 0 });
            sha.AppendData(File.ReadAllBytes(distributor));
            sha.AppendData(new byte[] { 0 });
            sha.AppendData(Encoding.UTF8.GetBytes(File.ReadAllText(password).Trim()));
            return new CertificateBackupProfile(
                Path.GetFileName(profileDir),
                Convert.ToHexString(sha.GetHashAndReset()),
                Convert.ToHexString(SHA256.HashData(authorBytes)));
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // The index, without the entries whose file is gone (deleted or moved by the user).
        private static List<CertificateBackupEntry> LoadIndex(string indexPath, ICertificateBackupTarget target)
        {
            try
            {
                if (!File.Exists(indexPath))
                    return new List<CertificateBackupEntry>();
                var entries = JsonSerializer.Deserialize<List<CertificateBackupEntry>>(File.ReadAllText(indexPath), JsonOptions)
                              ?? new List<CertificateBackupEntry>();
                return entries.Where(e => e?.Profiles is not null && !string.IsNullOrEmpty(e.Location) && target.Exists(e.Location)).ToList();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CertBackup] Index unreadable, starting over: {ex.Message}");
                return new List<CertificateBackupEntry>();
            }
        }

        private static void SaveIndex(string indexPath, List<CertificateBackupEntry> index)
        {
            var dir = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonOptions));
        }
    }
}
