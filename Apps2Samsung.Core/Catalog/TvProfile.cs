using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The apps on one TV that can be installed again from the catalog or a source (#1069): what a
    /// factory reset, a Smart Hub reset or a new TV takes away, written down so the TV can be
    /// rebuilt in one go. A small JSON file; nothing in it but names, ids and, only when the user
    /// asked for them, the values typed for packages that need some.
    /// </summary>
    public sealed class TvProfile
    {
        public const int CurrentSchema = 1;

        [JsonPropertyName("schema")]
        public int Schema { get; set; } = CurrentSchema;
        [JsonPropertyName("savedAtUtc")]
        public DateTimeOffset SavedAtUtc { get; set; }
        /// <summary>The TV the list was read from, as the user knows it; for telling two files apart, never matched on.</summary>
        [JsonPropertyName("tvName")]
        public string? TvName { get; set; }
        [JsonPropertyName("tvModel")]
        public string? TvModel { get; set; }
        [JsonPropertyName("tvIp")]
        public string? TvIp { get; set; }
        /// <summary>
        /// True when at least one app carries <see cref="TvProfileApp.Settings"/>. Those can be server
        /// addresses and API keys, so a reader can say so before the file is shared.
        /// </summary>
        [JsonPropertyName("containsSettings")]
        public bool ContainsSettings { get; set; }
        [JsonPropertyName("apps")]
        public List<TvProfileApp> Apps { get; set; } = new();
    }

    /// <summary>One app of a <see cref="TvProfile"/>: enough to find the same file in the catalog again.</summary>
    public sealed class TvProfileApp
    {
        /// <summary>The catalog app, as listed.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        /// <summary>The application id the file declares, as the catalog records it (not a renamed or derived id on the TV).</summary>
        [JsonPropertyName("appId")]
        public string AppId { get; set; } = "";
        /// <summary>The file that was on the TV, version and all; the next release's file of the same variant is found from it.</summary>
        [JsonPropertyName("packageFile")]
        public string PackageFile { get; set; } = "";
        [JsonPropertyName("variant")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Variant { get; set; }
        /// <summary>The source it came from, as <see cref="CatalogSource.Key"/>; the restore installs only from that source.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = "official";
        /// <summary>The version on the TV when the profile was saved; the restore installs the catalog's current one.</summary>
        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version { get; set; }
        /// <summary>
        /// The TV could not say which of several files it had and this machine had not installed it,
        /// so <see cref="PackageFile"/> is the catalog's first candidate, not a fact.
        /// </summary>
        [JsonPropertyName("uncertain")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Uncertain { get; set; }
        /// <summary>The values typed for a package that asks for some (#791), by field key. Only when the user chose to include them.</summary>
        [JsonPropertyName("settings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Settings { get; set; }
    }

    /// <summary>An app on the TV that a profile cannot carry, and why.</summary>
    public sealed record TvProfileLeftOut(string Title, string TizenId, TvProfileLeftOutReason Reason);

    public enum TvProfileLeftOutReason
    {
        /// <summary>No listed file declares its id: preinstalled, from the Samsung store, or a file installed by hand.</summary>
        NotInCatalog,
        /// <summary>The catalog stopped distributing it (#1033), so there is nothing to install again.</summary>
        RemovedFromCatalog,
    }

    public sealed record TvProfileSaved(TvProfile Profile, IReadOnlyList<TvProfileLeftOut> LeftOut);

    public enum TvRestoreState
    {
        /// <summary>Found in the catalog and not on the TV: this one gets installed.</summary>
        Ready,
        /// <summary>The same catalog app from the same source is on the TV already.</summary>
        AlreadyOnTv,
        /// <summary>The catalog (with the sources loaded now) no longer has it, or not from the source it came from.</summary>
        NotInCatalog,
    }

    /// <summary>One profile app against today's catalog and the TV being restored.</summary>
    /// <param name="VariantChanged">The variant the profile names is gone; <paramref name="Asset"/> is the app's file the catalog offers instead.</param>
    public sealed record TvRestoreItem(TvProfileApp App, TvRestoreState State, GitHubRelease? Release, Asset? Asset, bool VariantChanged = false);

    public static class TvProfiles
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        public static string Serialize(TvProfile profile) => JsonSerializer.Serialize(profile, Json);

        /// <summary>
        /// Reads a profile file. Throws <see cref="FormatException"/> with a sentence for the user
        /// when the text is not a profile, or is one written by a newer build with a newer schema.
        /// </summary>
        public static TvProfile Parse(string? json)
        {
            TvProfile? profile;
            try
            {
                // Any JSON object deserializes into an empty profile; one without an app list is some other file.
                using var doc = string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
                var isProfile = doc is { RootElement.ValueKind: JsonValueKind.Object } &&
                    doc.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "apps", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array);
                profile = isProfile ? JsonSerializer.Deserialize<TvProfile>(json!, Json) : null;
            }
            catch (JsonException ex)
            {
                throw new FormatException($"This is not a TV profile: {ex.Message}", ex);
            }
            if (profile is null || profile.Schema < 1 || profile.Apps is null)
                throw new FormatException("This is not a TV profile.");
            if (profile.Schema > TvProfile.CurrentSchema)
                throw new FormatException($"This TV profile was saved by a newer Apps2Samsung (format {profile.Schema}); this build reads format {TvProfile.CurrentSchema}.");
            profile.Apps.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.AppId) || string.IsNullOrWhiteSpace(a.Name));
            return profile;
        }

        /// <summary>
        /// The profile of a TV from its app list. Every app is matched to the catalog exactly as the
        /// installed-apps view does (<see cref="InstalledAppUpdates.Find"/>: renames, the remembered
        /// variant, the source it came from); what has no catalog entry is returned as left out.
        /// </summary>
        /// <param name="userSettingsJson">The head's saved values (<c>IAppConfig.AppUserSettingsJson</c>);
        /// read only when <paramref name="includeSettings"/> is true.</param>
        public static TvProfileSaved Save(IEnumerable<InstalledApp> installed, IReadOnlyList<GitHubRelease> catalog,
            IReadOnlyList<CommunityCatalogRemoved> removed, string? packageIdRenamesJson, string? installedVariantsJson,
            string? tvIp, string? tvDuid, string? userSettingsJson, bool includeSettings, DateTimeOffset now,
            string? tvName = null, string? tvModel = null)
        {
            var profile = new TvProfile { SavedAtUtc = now, TvName = Blank(tvName), TvModel = Blank(tvModel), TvIp = Blank(tvIp) };
            var leftOut = new List<TvProfileLeftOut>();

            foreach (var app in installed)
            {
                if (app is null || string.IsNullOrWhiteSpace(app.TizenId))
                    continue;
                if (RemovedApps.Find(app, removed, catalog, packageIdRenamesJson) is not null)
                {
                    leftOut.Add(new TvProfileLeftOut(app.DisplayName, app.TizenId, TvProfileLeftOutReason.RemovedFromCatalog));
                    continue;
                }
                var match = InstalledAppUpdates.Find(app, catalog, packageIdRenamesJson, installedVariantsJson, tvIp, tvDuid);
                if (match is null || string.IsNullOrWhiteSpace(match.Asset.AppId) || string.IsNullOrWhiteSpace(match.Release.Name))
                {
                    leftOut.Add(new TvProfileLeftOut(app.DisplayName, app.TizenId, TvProfileLeftOutReason.NotInCatalog));
                    continue;
                }

                var entry = new TvProfileApp
                {
                    Name = match.Release.Name,
                    AppId = match.Asset.AppId,
                    PackageFile = match.Asset.FileName,
                    Variant = Blank(match.Asset.Variant),
                    Source = match.Release.Source.Key,
                    Version = Blank(app.Version),
                    Uncertain = !match.VariantRemembered && Choices(match, catalog) > 1,
                };
                if (includeSettings && match.Asset.UserSettings is { Fields.Count: > 0 } &&
                    UserSettingsStore.Get(userSettingsJson, match.Asset.FileName)?.Values is { Count: > 0 } values)
                {
                    entry.Settings = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                    profile.ContainsSettings = true;
                }
                // Two tiles of one catalog file (the original and a renamed copy) restore as one install.
                if (!profile.Apps.Any(a => Same(a, entry)))
                    profile.Apps.Add(entry);
            }

            profile.Apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new TvProfileSaved(profile, leftOut);
        }

        /// <summary>
        /// What restoring <paramref name="profile"/> onto a TV comes to: each app found again in
        /// today's catalog (same app, same source, same variant where it still exists), and whether
        /// the TV has it already. <paramref name="onTv"/> is the target TV's app list; empty for a
        /// TV that was just reset.
        /// </summary>
        public static IReadOnlyList<TvRestoreItem> Plan(TvProfile profile, IReadOnlyList<GitHubRelease> catalog,
            IEnumerable<InstalledApp> onTv, string? packageIdRenamesJson = null, string? installedVariantsJson = null,
            string? tvIp = null, string? tvDuid = null)
        {
            var present = new List<CatalogMatch>();
            foreach (var app in onTv)
                if (InstalledAppUpdates.Find(app, catalog, packageIdRenamesJson, installedVariantsJson, tvIp, tvDuid) is { } m)
                    present.Add(m);

            var plan = new List<TvRestoreItem>();
            foreach (var app in profile.Apps)
            {
                var match = Resolve(app, catalog);
                if (match is null)
                {
                    plan.Add(new TvRestoreItem(app, TvRestoreState.NotInCatalog, null, null));
                    continue;
                }
                var has = present.Any(p =>
                    string.Equals(p.Release.Name, match.Release.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Release.Source.Key, match.Release.Source.Key, StringComparison.Ordinal));
                plan.Add(new TvRestoreItem(app, has ? TvRestoreState.AlreadyOnTv : TvRestoreState.Ready,
                    match.Release, match.Asset, VariantChanged: !match.VariantRemembered && Choices(match, catalog) > 1));
            }
            return plan;
        }

        // The profile entry is what the installed-variant store would have held for it, so the same
        // matcher that picks an update's file picks the restore's: the source it came from (or the
        // repository the catalog says it moved to), the app's newest release, the same variant.
        private static CatalogMatch? Resolve(TvProfileApp app, IReadOnlyList<GitHubRelease> catalog)
        {
            const string tv = "profile";
            var record = InstalledVariantStore.Set(null, new InstalledVariantRecord
            {
                TvIp = tv,
                AppId = app.AppId,
                CatalogApp = app.Name,
                Variant = app.Variant,
                PackageFile = string.IsNullOrWhiteSpace(app.PackageFile) ? app.Name : app.PackageFile,
                Source = string.IsNullOrWhiteSpace(app.Source) ? "official" : app.Source,
            });
            var asInstalled = new InstalledApp(app.Name, null, app.AppId, app.Version ?? string.Empty, string.Empty, true);
            var match = InstalledAppUpdates.Find(asInstalled, catalog, null, record, tv);
            if (match is null)
                return null;
            // Without the variant, the matcher falls back to any file that declares the id, which
            // can be another app's (the forks of one client share an id). Only this app's file counts.
            return string.Equals(match.Release.Name, app.Name, StringComparison.OrdinalIgnoreCase)
                ? match
                : catalog
                    .Where(r => string.Equals(r.Name, app.Name, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(r.Source.Key, match.Release.Source.Key, StringComparison.Ordinal))
                    .OrderByDescending(r => r.PublishedAt ?? string.Empty, StringComparer.Ordinal)
                    .SelectMany(r => r.Assets.Select(a => (Release: r, Asset: a)))
                    .Where(c => string.Equals(c.Asset.AppId, app.AppId, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new CatalogMatch(c.Release, c.Asset, CatalogVersionState.Unknown))
                    .FirstOrDefault();
        }

        // How many different files the TV's one id could stand for: other apps or other variants
        // declaring the same id, from the source the match is from.
        private static int Choices(CatalogMatch match, IReadOnlyList<GitHubRelease> catalog) =>
            catalog
                .Where(r => string.Equals(r.Source.Key, match.Release.Source.Key, StringComparison.Ordinal))
                .SelectMany(r => r.Assets
                    .Where(a => string.Equals(a.AppId, match.Asset.AppId, StringComparison.OrdinalIgnoreCase))
                    .Select(a => (r.Name ?? string.Empty) + "\n" +
                                 (string.IsNullOrWhiteSpace(a.Variant) ? InstalledVariantStore.Family(a.FileName) : a.Variant.Trim())))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        private static bool Same(TvProfileApp a, TvProfileApp b) =>
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Source, b.Source, StringComparison.Ordinal) &&
            string.Equals(a.AppId, b.AppId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Variant ?? InstalledVariantStore.Family(a.PackageFile), b.Variant ?? InstalledVariantStore.Family(b.PackageFile), StringComparison.OrdinalIgnoreCase);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
