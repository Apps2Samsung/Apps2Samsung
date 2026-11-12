using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Catalog
{
    /// <summary>One TV's app list as this device last wrote it down (#1069).</summary>
    public sealed class SavedTv
    {
        /// <summary>The TV as the head addresses it (its IP), like the installed-variant store.</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";
        [JsonPropertyName("profile")]
        public TvProfile Profile { get; set; } = new();
        /// <summary>When the TV's list was last read, whether or not that changed the profile.</summary>
        [JsonPropertyName("lastSeenUtc")]
        public DateTimeOffset LastSeenUtc { get; set; }
        /// <summary>The apps of <see cref="Profile"/> the TV did not have at <see cref="LastSeenUtc"/>, as <see cref="SavedTvs.AppKey"/>.</summary>
        [JsonPropertyName("missing")]
        public List<string> Missing { get; set; } = new();
        /// <summary>The <see cref="Missing"/> set the user answered "Not now" to; the offer stays away until the set changes.</summary>
        [JsonPropertyName("dismissed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Dismissed { get; set; }

        [JsonIgnore]
        public bool OfferRestore => Missing.Count > 0 && !string.Equals(Dismissed, SavedTvs.Signature(Missing), StringComparison.Ordinal);
    }

    /// <summary>
    /// The app list of every TV this device has read, kept by itself so there is no Save button to
    /// forget (#1069): each time a head reads a TV's apps it calls <see cref="Remember"/>. A list only
    /// grows on its own. When apps are gone from the TV (a reset, a new set on the same address) they
    /// stay in the saved list and are reported as missing, so they can be installed again; they leave
    /// the list when the user removes the app through Apps2Samsung, or forgets the TV.
    /// No values typed for a package are stored here: a restore on this device reads those from the
    /// head's own settings.
    /// </summary>
    public static class SavedTvs
    {
        public const string FileName = "saved-tvs.json";

        private static readonly object Gate = new();
        private static string? _path;
        private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

        /// <summary>Raised after the file changed, so an open Settings page can refresh.</summary>
        public static event Action? Changed;

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

        /// <summary>How two profile apps are told apart here: the catalog app and the source it came from.</summary>
        public static string AppKey(TvProfileApp app) =>
            (string.IsNullOrWhiteSpace(app.Source) ? "official" : app.Source) + "\n" + app.Name.Trim().ToLowerInvariant();

        internal static string Signature(IEnumerable<string> missing) =>
            string.Join("|", missing.OrderBy(m => m, StringComparer.Ordinal));

        public static IReadOnlyList<SavedTv> All()
        {
            lock (Gate)
                return Read();
        }

        public static SavedTv? Get(string? key)
        {
            lock (Gate)
                return Find(Read(), key);
        }

        /// <summary>
        /// The TV's list was just read: <paramref name="fresh"/> is its profile now. Returns what is
        /// kept for the TV, with <see cref="SavedTv.Missing"/> saying which saved apps the TV no longer
        /// has; null when there is nothing to keep (an empty TV that was never saved) or no head
        /// configured the store.
        /// </summary>
        public static SavedTv? Remember(string? key, TvProfile fresh, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(key) || fresh is null)
                return null;
            SavedTv? result;
            lock (Gate)
            {
                if (_path is null)
                    return null;
                var all = Read();
                var saved = Find(all, key);
                if (saved is null)
                {
                    if (fresh.Apps.Count == 0)
                        return null;
                    saved = new SavedTv { Key = key.Trim() };
                    all.Add(saved);
                }

                var onTv = new HashSet<string>(fresh.Apps.Select(AppKey), StringComparer.Ordinal);
                var gone = saved.Profile.Apps.Where(a => !onTv.Contains(AppKey(a))).ToList();
                if (gone.Count == 0)
                {
                    // Nothing was lost: the saved list becomes the TV as it is now.
                    saved.Profile = Without(fresh);
                    saved.Profile.SavedAtUtc = now;
                }
                else
                {
                    // Apps are gone. Keep them, at the date they were last all there, and take what
                    // the TV has now (new apps, newer versions) next to them.
                    var kept = Without(fresh);
                    kept.SavedAtUtc = saved.Profile.SavedAtUtc;
                    kept.TvName ??= saved.Profile.TvName;
                    kept.TvModel ??= saved.Profile.TvModel;
                    kept.Apps.AddRange(gone);
                    kept.Apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    saved.Profile = kept;
                }
                saved.Missing = gone.Select(AppKey).ToList();
                if (saved.Missing.Count == 0)
                    saved.Dismissed = null;
                saved.LastSeenUtc = now;
                Write(all);
                result = saved;
            }
            Changed?.Invoke();
            return result;
        }

        /// <summary>The user removed an app from the TV through Apps2Samsung: it is not something to restore.</summary>
        public static void RemoveApp(string? key, string? name, string? source)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            var appKey = AppKey(new TvProfileApp { Name = name, Source = source ?? "official" });
            Change(key, saved =>
            {
                saved.Profile.Apps.RemoveAll(a => AppKey(a) == appKey);
                saved.Missing.Remove(appKey);
            });
        }

        /// <summary>"Not now" on the offer: it stays away until other apps go missing.</summary>
        public static void Dismiss(string? key) => Change(key, saved => saved.Dismissed = Signature(saved.Missing));

        /// <summary>Drops the saved list of a TV.</summary>
        public static void Forget(string? key)
        {
            lock (Gate)
            {
                if (_path is null)
                    return;
                var all = Read();
                if (Find(all, key) is not { } saved)
                    return;
                all.Remove(saved);
                Write(all);
            }
            Changed?.Invoke();
        }

        private static void Change(string? key, Action<SavedTv> change)
        {
            lock (Gate)
            {
                if (_path is null)
                    return;
                var all = Read();
                if (Find(all, key) is not { } saved)
                    return;
                change(saved);
                Write(all);
            }
            Changed?.Invoke();
        }

        // A copy without the typed values: those stay in the head's own settings.
        private static TvProfile Without(TvProfile profile)
        {
            var copy = TvProfiles.Parse(TvProfiles.Serialize(profile));
            foreach (var app in copy.Apps)
                app.Settings = null;
            copy.ContainsSettings = false;
            return copy;
        }

        private static SavedTv? Find(List<SavedTv> all, string? key) =>
            string.IsNullOrWhiteSpace(key) ? null : all.FirstOrDefault(t => string.Equals(t.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

        private sealed class Store
        {
            [JsonPropertyName("tvs")]
            public List<SavedTv> Tvs { get; set; } = new();
        }

        private static List<SavedTv> Read()
        {
            if (_path is null || !File.Exists(_path))
                return new List<SavedTv>();
            try
            {
                var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_path), Json);
                var tvs = store?.Tvs ?? new List<SavedTv>();
                tvs.RemoveAll(t => t is null || string.IsNullOrWhiteSpace(t.Key) || t.Profile?.Apps is null);
                foreach (var tv in tvs)
                    tv.Missing ??= new List<string>();
                return tvs;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"[SavedTvs] Cannot read {_path}: {ex.Message}");
                return new List<SavedTv>();
            }
        }

        private static void Write(List<SavedTv> all)
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
                System.Diagnostics.Trace.WriteLine($"[SavedTvs] Cannot write {_path}: {ex.Message}");
            }
        }
    }
}
