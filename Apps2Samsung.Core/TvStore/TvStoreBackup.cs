using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.TvStore
{
    /// <summary>What a TV-Store backup says about itself, without reading into it.</summary>
    /// <param name="HasSecrets">It holds the signing certificates with their keys and the settings typed for apps on the TV.</param>
    /// <param name="MadeAt">When TV-Store made it, as TV-Store wrote it.</param>
    /// <param name="TvStoreVersion">The TV-Store that made it.</param>
    /// <param name="Tv">The TV it was made on, as TV-Store names it.</param>
    public sealed record TvStoreBackupInfo(bool HasSecrets, string? MadeAt, string? TvStoreVersion, string? Tv, string? Duid);

    public enum TvStoreRestoreState
    {
        /// <summary>The person at the TV pressed Restore.</summary>
        Restored,
        /// <summary>The TV asked and the answer was no (or the question went away unanswered).</summary>
        Declined,
        /// <summary>TV-Store did not take the backup at all: not one it reads, or made by a newer TV-Store.</summary>
        Refused,
    }

    /// <summary>How <c>A2SStore.restore</c> ended. The counts are what TV-Store says it took in, null when it did not say.</summary>
    public sealed record TvStoreRestoreResult(TvStoreRestoreState State, int? Certificates = null, int? Sources = null, int? UserSettings = null, string? Error = null);

    /// <summary>Why a head came back without a TV-Store backup. Not errors: the profile is saved without it.</summary>
    public enum TvStoreBackupSkipped
    {
        None,
        /// <summary>The TV does not list TV-Store.</summary>
        NotInstalled,
        /// <summary>A TV-Store without <c>A2SStore.backup</c> (0.3.40 and older).</summary>
        TooOld,
    }

    /// <summary>The backup TV-Store handed over, or why there is none.</summary>
    public sealed record TvStoreBackupTaken(JsonElement? Backup, TvStoreBackupSkipped Skipped, TvStoreBackupInfo? Info);

    /// <summary>
    /// TV-Store's own data in Backup &amp; restore (#1179, TV-Store#146): the pinned apps, its Settings,
    /// the sources, the signing certificates and the settings typed for apps on the TV. TV-Store makes
    /// the backup and takes it back itself (<c>A2SStore.backup</c>, <c>A2SStore.restore</c>); a head
    /// only carries the object, exactly as it got it: TV-Store reads every part again on a restore.
    /// </summary>
    public static class TvStoreBackup
    {
        public const string Format = "a2s-tvstore-backup";

        /// <summary>What <paramref name="backup"/> says about itself; null when it is not a TV-Store backup.</summary>
        public static TvStoreBackupInfo? Describe(JsonElement? backup)
        {
            if (backup is not { ValueKind: JsonValueKind.Object } b ||
                !b.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), Format, StringComparison.Ordinal))
                return null;

            // "secrets" is false only for a backup made without them; an older shape that does not say holds them.
            var secrets = !(b.TryGetProperty("secrets", out var s) && s.ValueKind == JsonValueKind.False);
            return new TvStoreBackupInfo(secrets, Text(b, "madeAt"), Text(b, "tvStore"), Text(b, "tv"), Text(b, "duid"));
        }

        /// <summary>
        /// The backup out of the reply of <c>A2SStore.backup()</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">TV-Store answered with an error, or with something that is not a backup; the message carries its words.</exception>
        public static JsonElement ParseBackupReply(JsonNode? reply)
        {
            if (reply?["ok"]?.GetValueKind() != JsonValueKind.True || reply["backup"] is not JsonObject backup)
                throw new InvalidOperationException(reply?["error"]?.ToString() ?? "no reply");
            var element = JsonSerializer.Deserialize<JsonElement>(backup.ToJsonString());
            if (Describe(element) is null)
                throw new InvalidOperationException("the reply is not a TV-Store backup");
            return element;
        }

        /// <summary>The three ways <c>A2SStore.restore()</c> answers.</summary>
        public static TvStoreRestoreResult ParseRestoreReply(JsonNode? reply)
        {
            var error = reply?["error"]?.ToString();
            if (reply?["ok"]?.GetValueKind() != JsonValueKind.True)
                return new TvStoreRestoreResult(TvStoreRestoreState.Refused, Error: error ?? "no reply");
            if (reply["restored"]?.GetValueKind() != JsonValueKind.True)
                return new TvStoreRestoreResult(TvStoreRestoreState.Declined, Error: error);
            var applied = reply["applied"];
            return new TvStoreRestoreResult(TvStoreRestoreState.Restored, Count(applied?["certificates"]), Count(applied?["sources"]), Count(applied?["userSettings"]));
        }

        /// <summary>
        /// Takes TV-Store's backup off a TV: starts TV-Store there (as arming does) and asks it.
        /// <paramref name="secrets"/> false leaves the certificates and the apps' settings out.
        /// </summary>
        /// <exception cref="InvalidOperationException">TV-Store could not be reached or did not answer with a backup.</exception>
        public static async Task<TvStoreBackupTaken> TakeAsync(ISdbEngine sdb, string tvIp, bool secrets,
            IProgress<string>? progress = null, string? packageIdRenamesJson = null, CancellationToken ct = default)
        {
            TvStoreClient store;
            try
            {
                store = await TvStoreClient.AttachAsync(sdb, tvIp, progress, packageIdRenamesJson, ct).ConfigureAwait(false);
            }
            catch (TvStoreNotInstalledException)
            {
                return new TvStoreBackupTaken(null, TvStoreBackupSkipped.NotInstalled, null);
            }
            await using (store)
            {
                if (!await store.SupportsBackupAsync(ct).ConfigureAwait(false))
                    return new TvStoreBackupTaken(null, TvStoreBackupSkipped.TooOld, null);
                var backup = await store.BackupAsync(secrets, ct).ConfigureAwait(false);
                return new TvStoreBackupTaken(backup, TvStoreBackupSkipped.None, Describe(backup));
            }
        }

        /// <summary>
        /// Hands <paramref name="backup"/> to TV-Store on a TV. Comes back when the person at the TV
        /// answered its question, which can take minutes; TV-Store starts again after a restore.
        /// </summary>
        /// <exception cref="TvStoreNotInstalledException">TV-Store is not on the TV (yet).</exception>
        /// <exception cref="NotSupportedException">The TV-Store there has no restore (0.3.40 and older).</exception>
        public static async Task<TvStoreRestoreResult> RestoreAsync(ISdbEngine sdb, string tvIp, JsonElement backup,
            IProgress<string>? progress = null, string? packageIdRenamesJson = null, CancellationToken ct = default)
        {
            await using var store = await TvStoreClient.AttachAsync(sdb, tvIp, progress, packageIdRenamesJson, ct).ConfigureAwait(false);
            if (!await store.SupportsBackupAsync(ct).ConfigureAwait(false))
                throw new NotSupportedException("This TV-Store has no backup and restore yet.");
            progress?.Report(ConfirmOnTvKey);
            return await store.RestoreAsync(backup, ct).ConfigureAwait(false);
        }

        /// <summary>Progress key: the TV now shows what the backup holds and waits for Restore.</summary>
        public const string ConfirmOnTvKey = "statusTvStoreRestoreConfirmOnTv";
        /// <summary>Progress key: TV-Store is being asked for its backup.</summary>
        public const string TakingKey = "statusTvStoreBackupTaking";

        /// <summary>The last TV-Store without the two calls (TV-Store#147 comes in the release after it).</summary>
        public static readonly Version LastWithout = new(0, 3, 40);

        /// <summary>
        /// Whether asking is worth it, from the TV's app list alone: taking a backup starts TV-Store
        /// again on the TV, which a TV-Store that cannot answer should be spared.
        /// <see cref="TvStoreBackupSkipped.None"/> means "ask it"; a version that cannot be read counts as that.
        /// </summary>
        public static TvStoreBackupSkipped CheckOnTv(IReadOnlyList<Apps2Samsung.Models.InstalledApp> apps, string? packageIdRenamesJson = null)
        {
            var found = TvStorePackage.Find(apps, packageIdRenamesJson);
            if (!found.IsInstalled || found.AppId is null)
                return TvStoreBackupSkipped.NotInstalled;
            var listed = apps.FirstOrDefault(a => string.Equals(a.TizenId, found.AppId, StringComparison.OrdinalIgnoreCase));
            return Version.TryParse(listed?.Version, out var version) && version <= LastWithout
                ? TvStoreBackupSkipped.TooOld
                : TvStoreBackupSkipped.None;
        }

        /// <summary>
        /// <see cref="TakeAsync"/> for a head that saves a profile either way: never throws (except on
        /// cancel), and says in the user's language what became of TV-Store's part.
        /// <paramref name="localize"/> is the head's string lookup.
        /// </summary>
        public static async Task<(TvStoreBackupTaken Taken, string Message)> TryTakeAsync(ISdbEngine sdb, string tvIp, bool secrets,
            Func<string, string> localize, IProgress<string>? progress = null, string? packageIdRenamesJson = null, CancellationToken ct = default)
        {
            try
            {
                progress?.Report(TakingKey);
                var taken = await TakeAsync(sdb, tvIp, secrets, progress, packageIdRenamesJson, ct).ConfigureAwait(false);
                return (taken, SavedMessage(taken, localize));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] no backup taken from {tvIp}: {ex}");
                return (new TvStoreBackupTaken(null, TvStoreBackupSkipped.None, null), string.Format(localize("statusTvStoreBackupFailed"), ex.Message));
            }
        }

        /// <summary>What became of TV-Store's part of a saved profile, in the user's language.</summary>
        public static string SavedMessage(TvStoreBackupTaken taken, Func<string, string> localize) => taken switch
        {
            { Info.HasSecrets: true } => localize("statusTvStoreBackupIncludedSecrets"),
            { Info: not null } => localize("statusTvStoreBackupIncluded"),
            { Skipped: TvStoreBackupSkipped.TooOld } => localize("statusTvStoreBackupTooOld"),
            _ => localize("statusTvStoreBackupNotInstalled"),
        };

        /// <summary>
        /// <see cref="RestoreAsync"/> for a head: never throws (except on cancel). True when the TV
        /// took the backup; the message says what happened either way, in the user's language.
        /// </summary>
        public static async Task<(bool Restored, string Message)> TryRestoreAsync(ISdbEngine sdb, string tvIp, JsonElement backup,
            Func<string, string> localize, IProgress<string>? progress = null, string? packageIdRenamesJson = null, CancellationToken ct = default)
        {
            try
            {
                var result = await RestoreAsync(sdb, tvIp, backup, progress, packageIdRenamesJson, ct).ConfigureAwait(false);
                return (result.State == TvStoreRestoreState.Restored, RestoreMessage(result, localize));
            }
            catch (TvStoreNotInstalledException)
            {
                return (false, localize("statusTvStoreRestoreNotInstalled"));
            }
            catch (NotSupportedException)
            {
                return (false, localize("statusTvStoreRestoreTooOld"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine($"[tvstore] restore on {tvIp} failed: {ex}");
                return (false, string.Format(localize("statusTvStoreRestoreFailed"), ex.Message));
            }
        }

        /// <summary>How a restore ended, in the user's language.</summary>
        public static string RestoreMessage(TvStoreRestoreResult result, Func<string, string> localize) => result.State switch
        {
            TvStoreRestoreState.Restored when result.Certificates is { } c && result.Sources is { } s && result.UserSettings is { } u =>
                localize("statusTvStoreRestoreDone") + " " + string.Format(localize("statusTvStoreRestoreApplied"), c, s, u),
            TvStoreRestoreState.Restored => localize("statusTvStoreRestoreDone"),
            TvStoreRestoreState.Declined => localize("statusTvStoreRestoreDeclined"),
            _ => string.Format(localize("statusTvStoreRestoreRefused"), result.Error ?? "-"),
        };

        private static string? Text(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

        // TV-Store reports counts; a list or a yes/no would still say something.
        private static int? Count(JsonNode? node) => node switch
        {
            JsonValue v when v.TryGetValue<int>(out var n) => n,
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
            JsonArray a => a.Count,
            JsonObject o => o.Count,
            _ => null,
        };
    }

    /// <summary>One TV's TV-Store backup as this device keeps it.</summary>
    public sealed class SavedTvStoreBackup
    {
        /// <summary>The TV as the head addresses it (its IP), like <see cref="Apps2Samsung.Catalog.SavedTv.Key"/>.</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";
        [JsonPropertyName("savedAtUtc")]
        public DateTimeOffset SavedAtUtc { get; set; }
        /// <summary>The object TV-Store made, untouched.</summary>
        [JsonPropertyName("backup")]
        public JsonElement Backup { get; set; }
    }

    /// <summary>
    /// The last TV-Store backup taken of each TV (#1179), in the head's data folder next to the saved
    /// app lists, so the Backup &amp; restore zip can carry it and a restore from a saved list finds it.
    /// It can hold signing keys and passwords (<see cref="TvStoreBackupInfo.HasSecrets"/>), like the
    /// certificate store and the settings beside it. One per TV: a newer one replaces the older.
    /// </summary>
    public static class TvStoreBackups
    {
        public const string FileName = "tvstore-backups.json";

        private static readonly object Gate = new();
        private static string? _path;
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

        public static bool IsConfigured => _path is not null;

        /// <summary>Called once at startup by each head, with the file in its data folder.</summary>
        public static void Configure(string path)
        {
            lock (Gate)
                _path = path;
        }

        /// <summary>For tests: back to "no head configured".</summary>
        public static void Reset()
        {
            lock (Gate)
                _path = null;
        }

        public static SavedTvStoreBackup? Get(string? key)
        {
            lock (Gate)
                return Find(Read(), key);
        }

        /// <summary>Keeps <paramref name="backup"/> as this TV's; something that is not a TV-Store backup is not kept.</summary>
        public static void Set(string? key, JsonElement backup, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(key) || TvStoreBackup.Describe(backup) is null)
                return;
            lock (Gate)
            {
                if (_path is null)
                    return;
                var all = Read();
                all.RemoveAll(b => string.Equals(b.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
                all.Add(new SavedTvStoreBackup { Key = key.Trim(), SavedAtUtc = now, Backup = backup.Clone() });
                Write(all);
            }
        }

        public static void Forget(string? key)
        {
            lock (Gate)
            {
                if (_path is null)
                    return;
                var all = Read();
                if (all.RemoveAll(b => string.Equals(b.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
                    Write(all);
            }
        }

        /// <summary>Every kept backup as one JSON text, for the zip; null when there is none or no head configured the store.</summary>
        public static string? ExportJson()
        {
            lock (Gate)
            {
                if (_path is null)
                    return null;
                var all = Read();
                return all.Count == 0 ? null : JsonSerializer.Serialize(new Store { Tvs = all }, Json);
            }
        }

        /// <summary>
        /// Takes the backups of a zip in: one for a TV this device has none of is added, one it has is
        /// replaced only by a newer one. Returns how many came in.
        /// </summary>
        public static int Merge(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return 0;
            List<SavedTvStoreBackup> incoming;
            try
            {
                incoming = JsonSerializer.Deserialize<Store>(json, Json)?.Tvs ?? new List<SavedTvStoreBackup>();
            }
            catch (JsonException)
            {
                return 0;
            }
            incoming.RemoveAll(b => b is null || string.IsNullOrWhiteSpace(b.Key) || TvStoreBackup.Describe(b.Backup) is null);

            var taken = 0;
            lock (Gate)
            {
                if (_path is null)
                    return 0;
                var all = Read();
                foreach (var backup in incoming)
                {
                    var known = Find(all, backup.Key);
                    if (known is not null && known.SavedAtUtc >= backup.SavedAtUtc)
                        continue;
                    if (known is not null)
                        all.Remove(known);
                    all.Add(backup);
                    taken++;
                }
                if (taken > 0)
                    Write(all);
            }
            return taken;
        }

        private static SavedTvStoreBackup? Find(List<SavedTvStoreBackup> all, string? key) =>
            string.IsNullOrWhiteSpace(key) ? null : all.FirstOrDefault(b => string.Equals(b.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

        private sealed class Store
        {
            [JsonPropertyName("tvs")]
            public List<SavedTvStoreBackup> Tvs { get; set; } = new();
        }

        private static List<SavedTvStoreBackup> Read()
        {
            if (_path is null || !File.Exists(_path))
                return new List<SavedTvStoreBackup>();
            try
            {
                var tvs = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), Json)?.Tvs ?? new List<SavedTvStoreBackup>();
                tvs.RemoveAll(b => b is null || string.IsNullOrWhiteSpace(b.Key) || TvStoreBackup.Describe(b.Backup) is null);
                return tvs;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"[TvStoreBackups] Cannot read {_path}: {ex.Message}");
                return new List<SavedTvStoreBackup>();
            }
        }

        private static void Write(List<SavedTvStoreBackup> all)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path!);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Store { Tvs = all }, Json));
                File.Move(temp, _path!, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"[TvStoreBackups] Cannot write {_path}: {ex.Message}");
            }
        }
    }
}
