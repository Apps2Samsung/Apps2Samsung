using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One row in the Application-settings "App icons" editor: an installable app and the launcher
    /// icon chosen for it. <see cref="Value"/> is persisted in <c>AppSettings.CustomAppIconsJson</c>
    /// keyed by <see cref="Key"/> — "" (default), "oblong" (bundled 16:9 tile), or a custom PNG path.
    /// </summary>
    public partial class AppIconEntry : ObservableObject
    {
        public const string OblongValue = "oblong";

        public string DisplayName { get; init; } = string.Empty;

        /// <summary>Token matched (case-insensitive, substring) against the package file name at install.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>True when this app ships a bundled 16:9 "oblong" tile (so the preset is offered).</summary>
        public bool HasOblong { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOblong))]
        [NotifyPropertyChangedFor(nameof(IsCustom))]
        [NotifyPropertyChangedFor(nameof(IsDefault))]
        private string value = string.Empty;

        /// <summary>Localized one-line description of the current choice (set by the view model).</summary>
        [ObservableProperty]
        private string summary = string.Empty;

        /// <summary>Custom launcher title for this app. "" keeps the package's own name; otherwise
        /// persisted in <c>AppSettings.CustomAppTitlesJson</c> keyed by <see cref="Key"/> and written
        /// into the package's config.xml &lt;name&gt; at install.</summary>
        [ObservableProperty]
        private string title = string.Empty;

        public bool IsOblong => string.Equals(Value, OblongValue, StringComparison.OrdinalIgnoreCase);
        public bool IsCustom => !string.IsNullOrEmpty(Value) && !IsOblong;
        public bool IsDefault => string.IsNullOrEmpty(Value);
    }
}
