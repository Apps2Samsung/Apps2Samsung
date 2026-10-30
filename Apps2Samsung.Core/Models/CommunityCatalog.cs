using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// The <c>catalog.json</c> asset the tizen-community-packages release ships next to its
    /// .wgt/.tpk files: one entry per file, carrying the package's display name, category and
    /// (for forks and per-Tizen builds of the same app) the group it folds into. Built by
    /// <c>scripts/build-catalog.sh</c> in that repo; a release without it (older bundles) simply
    /// yields the flat, uncategorised list the installer showed before.
    /// </summary>
    public sealed class CommunityCatalog
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Category id → English display name, the fallback when a head has no translation for an id.</summary>
        public Dictionary<string, string> Categories { get; set; } = new();

        public List<CommunityCatalogEntry> Apps { get; set; } = new();
    }

    public sealed class CommunityCatalogEntry
    {
        /// <summary>The file name inside the bundle (the release asset name), the key the installer matches on.</summary>
        public string File { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>One of the ids in <see cref="AppCategories"/>; anything unknown lands in "other".</summary>
        public string Category { get; set; } = "";
        /// <summary>When set, every file with the same group becomes one app entry with a variant picker.</summary>
        public string? Group { get; set; }
        /// <summary>Label of this file inside its group ("OneLiberty · Chrome", "Tizen 8").</summary>
        public string? Variant { get; set; }
        public string Repo { get; set; } = "";
        public string Host { get; set; } = "github";

        /// <summary>
        /// What the file's own manifest says (config.xml / tizen-manifest.xml), read when the bundle is
        /// assembled: the version the TV reports once the file is installed, and the ids an installed
        /// app is matched on (#820). Null in a bundle built before these were recorded.
        /// </summary>
        public string? Version { get; set; }
        [JsonPropertyName("app_id")]
        public string? AppId { get; set; }
        [JsonPropertyName("package_id")]
        public string? PackageId { get; set; }

        /// <summary>
        /// The Tizen version the file asks for (<c>required_version</c> in config.xml, <c>api-version</c>
        /// in tizen-manifest.xml), read with the three above. A TV older than this refuses the package
        /// with install failed[118, -19] (TV-Store#44), so TV-Store labels such an app and stops the
        /// install before the push. Null in a bundle built before it was recorded.
        /// </summary>
        [JsonPropertyName("required_version")]
        public string? RequiredVersion { get; set; }

        /// <summary>
        /// Values the user has to supply before this file installs (a per-user API key, a server
        /// address), written into the package by <c>UserSettingsPatcher</c>. Null for the usual
        /// package that needs nothing (#791).
        /// </summary>
        [JsonPropertyName("user_settings")]
        public UserSettingsSpec? UserSettings { get; set; }

        /// <summary>
        /// True for a package meant for adults only (#850). Independent of the category: the app
        /// keeps the category that says what it is. The heads leave such entries out of the list
        /// until the user turns "Show adult (18+) apps" on. The key is only written when true, the
        /// same shape the packages repo emits, so the snapshot round-trips it as-is.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Adult { get; set; }

        /// <summary>
        /// Install-time properties the heads warn about before installing (<c>flags</c> in the
        /// manifest). Null for the usual package; written only when present, so the snapshot
        /// round-trips it as-is.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Flags { get; set; }

        /// <summary>
        /// The package gains root on the TV and runs a service from boot (<c>flags: ["root"]</c>).
        /// After install the user points Developer Mode's Host PC IP at 127.0.0.1, which locks every
        /// PC out of SDB until it is set back, so the heads confirm first.
        /// </summary>
        [JsonIgnore]
        public bool IsRoot => Flags?.Exists(f => string.Equals(f, "root", System.StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// A package's <c>user_settings</c> block: the fields whose values go into the package by
    /// exact-text replacement. Two forms, never mixed: <see cref="File"/> plus find/replace on every
    /// field (one file), or <see cref="UserSettingsField.Targets"/> on every field and no file (a
    /// value that lands in several places). The schema lives in tizen-community-packages
    /// (<c>packages.schema.json</c>); the catalog copies the block through as-is.
    /// </summary>
    public sealed class UserSettingsSpec
    {
        /// <summary>A page that explains where the values come from; shown as a link above the form.</summary>
        [JsonPropertyName("help_url")]
        public string? HelpUrl { get; set; }

        /// <summary>The file to patch, relative to the package root. Empty when the fields use targets.</summary>
        public string File { get; set; } = "";

        public List<UserSettingsField> Fields { get; set; } = new();

        /// <summary>
        /// Whether the block can be applied: at least one field, and either a file with find/replace
        /// on every field or targets on every field. Anything else is a block this version can't
        /// write and is dropped, so the package installs as if it declared none.
        /// </summary>
        [JsonIgnore]
        public bool IsUsable =>
            Fields.Count > 0
            && (string.IsNullOrWhiteSpace(File)
                ? Fields.TrueForAll(f => f.Targets is { Count: > 0 } t && t.TrueForAll(x => x.IsUsable))
                : Fields.TrueForAll(f => f.Targets is null && !string.IsNullOrEmpty(f.Find)));
    }

    /// <summary>One place a field's value goes: a file in the package and the exact text replaced there.</summary>
    public sealed class UserSettingsTarget
    {
        public string File { get; set; } = "";
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";

        [JsonIgnore]
        public bool IsUsable => !string.IsNullOrWhiteSpace(File) && !string.IsNullOrEmpty(Find);
    }

    public sealed class UserSettingsField
    {
        /// <summary>Stable id the value is stored under, per package.</summary>
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary><c>number</c> (digits only) or <c>text</c>.</summary>
        public string Type { get; set; } = "text";
        public bool Required { get; set; } = true;
        /// <summary>A regex the value must match, when the manifest gives one.</summary>
        public string? Pattern { get; set; }
        /// <summary>The exact text in the file that is replaced; the install fails when it is missing.</summary>
        public string Find { get; set; } = "";
        /// <summary>What replaces it; <c>{value}</c> is the user's value.</summary>
        public string Replace { get; set; } = "";
        /// <summary>Every place the value goes, instead of the spec's file plus <see cref="Find"/>/<see cref="Replace"/>.</summary>
        public List<UserSettingsTarget>? Targets { get; set; }
        /// <summary>Written instead when the field is optional and left empty, so every find is always replaced.</summary>
        public string? Default { get; set; }
        /// <summary>number only: the smallest and largest accepted value, inclusive.</summary>
        public long? Min { get; set; }
        public long? Max { get; set; }
        /// <summary>number only: values the form rejects (ports the TV itself uses).</summary>
        public List<long>? Disallow { get; set; }

        public bool IsNumber => string.Equals(Type, "number", System.StringComparison.OrdinalIgnoreCase);
    }
}
