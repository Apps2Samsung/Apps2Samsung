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
    }

    /// <summary>
    /// A package's <c>user_settings</c> block: one file in the package, and the fields whose values
    /// go into it by exact-text replacement. The schema lives in tizen-community-packages
    /// (<c>packages.schema.json</c>); the catalog copies the block through as-is.
    /// </summary>
    public sealed class UserSettingsSpec
    {
        /// <summary>A page that explains where the values come from; shown as a link above the form.</summary>
        [JsonPropertyName("help_url")]
        public string? HelpUrl { get; set; }

        /// <summary>The file to patch, relative to the package root.</summary>
        public string File { get; set; } = "";

        public List<UserSettingsField> Fields { get; set; } = new();
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

        public bool IsNumber => string.Equals(Type, "number", System.StringComparison.OrdinalIgnoreCase);
    }
}
