using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Catalog
{
    /// <summary>One copy of a TV's app list the app wrote.</summary>
    /// <param name="Key">The TV, as <see cref="SavedTv.Key"/>.</param>
    /// <param name="Fingerprint">Which apps the list held (<see cref="TvBackups.Fingerprint"/>): the same value means nothing to save again.</param>
    public sealed record TvBackupEntry(string Key, string TvName, string FileName, string Location, DateTime CreatedUtc, string Fingerprint, int Apps);

    /// <summary>What Settings › Backup shows about the copies.</summary>
    public sealed record TvBackupStatus(IReadOnlyList<TvBackupEntry> Entries, string DisplayLocation, string? LastError)
    {
        /// <summary>The newest copy of one TV's list, or null when there is none (any more).</summary>
        public TvBackupEntry? LatestFor(string? key) => Entries
            .Where(e => string.Equals(e.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Automatic copies of each TV's app list (#1177). <see cref="SavedTvs"/> already keeps the list of
    /// every TV by itself, but inside the app's own data folder: a reinstall, a new computer or a
    /// cleared profile takes it along, and that is found out after the TV was wiped. Each head points
    /// this at a place outside its data folder (<see cref="Configure"/>, the same place its certificate
    /// backups go); from then on a list is written there as a file whenever the apps in it changed.
    /// <para>
    /// A copy is a plain TV profile (<see cref="FileExtension"/>), the file "Restore a TV from a file"
    /// and the CLI's <c>tv restore</c> read. It never holds the values typed for a package: those can
    /// be server addresses and keys, and this folder is one people sync. The last <see cref="Keep"/>
    /// per TV are kept. Like the certificate backups, the heads keep an index of what they wrote
    /// instead of listing the folder, so a target that can write but not list (Android's Downloads)
    /// works the same.
    /// </para>
    /// <para>
    /// A TV that lost its apps does not get a smaller copy: <see cref="SavedTvs"/> keeps the missing
    /// apps in the saved list, so the list, its fingerprint and the newest file stay as they were.
    /// </para>
    /// <para>
    /// A copy also carries TV-Store's own backup of that TV (#1179) when this device has one that
    /// was taken without the certificates and the apps' settings
    /// (<see cref="Apps2Samsung.TvStore.TvStoreBackups"/>). One with them stays out: same rule as the
    /// typed values. A TV-Store that hands its backup out by itself (TV-Store#149, <c>GET
    /// /backup/public</c>) is asked for it each time the list is written, and that one goes first:
    /// it has no keys by design and is as fresh as the TV. Without it, it is the last backup
    /// someone took.
    /// </para>
    /// </summary>
    public static class TvBackups
    {
        public const int Keep = 5;
        public const string FilePrefix = "a2s-tv-";
        public const string FileExtension = ".a2stv";
        public const string IndexFileName = "tv-backups.json";

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
        private static readonly object Gate = new();
        private static string? _indexPath;
        private static ICertificateBackupTarget? _target;
        private static string? _lastError;
        private static Task _pending = Task.CompletedTask;

        /// <summary>Raised after a copy was written, or a write failed, so an open Settings page can refresh.</summary>
        public static event Action? StatusChanged;

        public static bool IsConfigured => _target is not null;

        /// <summary>Called once at startup by each head.</summary>
        public static void Configure(string indexPath, ICertificateBackupTarget target)
        {
            lock (Gate)
            {
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
                _indexPath = null;
                _target = null;
                _lastError = null;
            }
        }

        /// <summary>
        /// The desktop and CLI folder: Documents\Apps2Samsung\TV backups, or the data folder when the
        /// system has no Documents (a server, Termux).
        /// </summary>
        public static string DefaultFolder(string dataDir)
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)
                ? Path.Combine(dataDir, "TV backups")
                : Path.Combine(documents, "Apps2Samsung", "TV backups");
        }

        /// <summary>
        /// A TV's saved list was just written: copy it out when its apps are not the ones of the newest
        /// copy. Runs off the caller's thread and never throws; a failure is logged and shown in Settings.
        /// </summary>
        public static void Changed(SavedTv? tv)
        {
            if (!IsConfigured || tv is null)
                return;
            // A copy of its own: the caller goes on using (and changing) the list it was handed.
            var key = tv.Key;
            var profile = Without(tv.Profile);
            lock (Gate)
            {
                // One after the other, so two reads of a TV in a row cannot write two files.
                _pending = _pending.ContinueWith(_ =>
                {
                    try
                    {
                        BackUp(key, profile);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[TvBackup] Automatic copy of {key} failed: {ex.Message}");
                    }
                }, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Waits for the copies <see cref="Changed"/> started. For a head that exits right after reading
        /// a TV (the CLI): the apps keep running and have no need to wait.
        /// </summary>
        public static void WaitForPending(TimeSpan timeout)
        {
            Task pending;
            lock (Gate)
                pending = _pending;
            try { pending.Wait(timeout); } catch { /* a failed copy was logged where it failed */ }
        }

        public static TvBackupStatus? Status()
        {
            lock (Gate)
            {
                if (_target is null || _indexPath is null)
                    return null;
                return new TvBackupStatus(LoadIndex(_indexPath, _target), _target.DisplayLocation, _lastError);
            }
        }

        private static void BackUp(string key, TvProfile profile)
        {
            // Ask TV-Store on the TV first, outside the lock: it is a request on the network. No
            // answer changes nothing, the one kept from last time rides along.
            if (Apps2Samsung.TvStore.TvStorePublicBackup.TryFetch(key) is { } fresh)
                Apps2Samsung.TvStore.TvStoreBackups.KeepFromTv(key, fresh, DateTimeOffset.UtcNow);
            var tvStore = (Apps2Samsung.TvStore.TvStoreBackups.GetFromTv(key) ?? Apps2Samsung.TvStore.TvStoreBackups.Get(key))?.Backup;

            TvBackupEntry? written;
            lock (Gate)
            {
                if (_target is null || _indexPath is null)
                    return;
                try
                {
                    written = BackUp(_indexPath, _target, key, profile, DateTime.UtcNow, tvStore);
                    if (written is null && _lastError is null)
                        return;
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
                Trace.WriteLine($"[TvBackup] Saved the app list of {key} ({written.Apps} apps) to {written.Location}");
            StatusChanged?.Invoke();
        }

        // ---- The parts with no global state, which the tests drive ----

        /// <summary>
        /// Writes <paramref name="profile"/> as a file when the newest copy of this TV holds other apps
        /// (or there is none), records it in the index and prunes. Null when there was nothing to do:
        /// an empty list, or the same apps as last time.
        /// </summary>
        /// <param name="tvStore">TV-Store's own backup of this TV as the device keeps it (#1179). It goes
        /// into the file only when it holds no certificates or app settings; a newer one is a reason
        /// for a new file.</param>
        public static TvBackupEntry? BackUp(string indexPath, ICertificateBackupTarget target, string key, TvProfile profile, DateTime nowUtc,
            System.Text.Json.JsonElement? tvStore = null)
        {
            if (string.IsNullOrWhiteSpace(key) || profile?.Apps is null || profile.Apps.Count == 0)
                return null;
            key = key.Trim();

            var index = LoadIndex(indexPath, target);
            var tvStoreInfo = Apps2Samsung.TvStore.TvStoreBackup.Describe(tvStore);
            if (tvStoreInfo is { HasSecrets: true })
                tvStoreInfo = null;
            // The apps, and which TV-Store backup rides along: either changing is a new file.
            var fingerprint = Fingerprint(profile) + (tvStoreInfo is null ? string.Empty : "+tvstore:" + (tvStoreInfo.MadeAt ?? "?"));
            var newest = index
                .Where(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedUtc)
                .FirstOrDefault();
            if (newest is not null && newest.Fingerprint == fingerprint)
                return null;

            var copy = Without(profile);
            if (tvStoreInfo is not null)
                TvProfiles.SetTvStore(copy, tvStore);
            var name = string.IsNullOrWhiteSpace(copy.TvName) ? key : copy.TvName!.Trim();
            var stem = $"{FilePrefix}{SafeName(name)}-{nowUtc.ToLocalTime():yyyy-MM-dd-HHmmss}";
            var fileName = stem + FileExtension;
            for (var n = 2; index.Any(e => e.FileName == fileName); n++)
                fileName = $"{stem}-{n}{FileExtension}";

            var location = target.Write(fileName, Encoding.UTF8.GetBytes(TvProfiles.Serialize(copy)));
            var entry = new TvBackupEntry(key, name, fileName, location, nowUtc, fingerprint, copy.Apps.Count);
            index.RemoveAll(e => e.Location == location);
            index.Add(entry);
            index = Prune(index, target);
            SaveIndex(indexPath, index);
            return entry;
        }

        /// <summary>
        /// Which apps a list holds: each app's source, name and variant. Versions are left out on
        /// purpose, a restore installs the catalog's current one, so an update of an app is not a
        /// reason for another file.
        /// </summary>
        public static string Fingerprint(TvProfile profile)
        {
            var lines = profile.Apps
                .Select(a => SavedTvs.AppKey(a) + "\n" + (a.Variant ?? string.Empty).Trim().ToLowerInvariant())
                .OrderBy(line => line, StringComparer.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n\n", lines))));
        }

        // The newest Keep of each TV stay.
        private static List<TvBackupEntry> Prune(List<TvBackupEntry> index, ICertificateBackupTarget target)
        {
            var kept = new List<TvBackupEntry>();
            foreach (var tv in index.GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = tv.OrderByDescending(e => e.CreatedUtc).ToList();
                kept.AddRange(ordered.Take(Keep));
                foreach (var old in ordered.Skip(Keep))
                    target.Delete(old.Location);
            }
            return kept;
        }

        // No typed values in a copy, whatever the caller handed over.
        private static TvProfile Without(TvProfile profile)
        {
            var copy = TvProfiles.Parse(TvProfiles.Serialize(profile));
            foreach (var app in copy.Apps)
                app.Settings = null;
            copy.ContainsSettings = false;
            // Whatever the caller's list carried: only BackUp decides what of TV-Store's goes in.
            copy.TvStore = null;
            copy.TvStoreHasSecrets = false;
            return copy;
        }

        // A TV's name as part of a file name: letters, digits, dot, dash and underscore stay.
        private static string SafeName(string name)
        {
            var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
            while (safe.Contains("--", StringComparison.Ordinal))
                safe = safe.Replace("--", "-", StringComparison.Ordinal);
            if (safe.Length > 40)
                safe = safe.Substring(0, 40).Trim('-');
            return safe.Length == 0 ? "tv" : safe;
        }

        private static List<TvBackupEntry> LoadIndex(string indexPath, ICertificateBackupTarget target)
        {
            try
            {
                if (!File.Exists(indexPath))
                    return new List<TvBackupEntry>();
                var entries = JsonSerializer.Deserialize<List<TvBackupEntry>>(File.ReadAllText(indexPath), JsonOptions)
                              ?? new List<TvBackupEntry>();
                // A file the user deleted or moved is no copy any more: the next change writes a new one.
                return entries.Where(e => e is not null && !string.IsNullOrEmpty(e.Key) && !string.IsNullOrEmpty(e.Location) && target.Exists(e.Location)).ToList();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TvBackup] Index unreadable, starting over: {ex.Message}");
                return new List<TvBackupEntry>();
            }
        }

        private static void SaveIndex(string indexPath, List<TvBackupEntry> index)
        {
            var dir = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonOptions));
        }
    }
}
