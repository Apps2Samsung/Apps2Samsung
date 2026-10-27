using System.Globalization;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One app reported installed on a TV by the <c>vd_applist</c> query, parsed by
    /// <see cref="Apps2Samsung.Sdb.TizenInstalledApps"/>. Shared by both heads' "installed apps" view.
    /// </summary>
    public sealed record InstalledApp(
        string Title,
        string? AppId,
        string TizenId,
        string Version,
        string InstallDate,
        bool IsRemovable,
        long SizeBytes = 0,
        string? IconUrl = null,
        string? CatalogLine = null,
        bool HasUpdate = false)
    {
        /// <summary>Title if the TV reported one, otherwise the Tizen id — never empty.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? TizenId : Title;

        public bool HasIcon => !string.IsNullOrWhiteSpace(IconUrl);

        /// <summary>
        /// What the catalog says about this app, already in the user's language ("Version 2.1.0 is
        /// available in the catalog"); null when the app is not in the catalog (#820). Filled by the
        /// head from <c>InstalledAppUpdates</c>, together with <see cref="HasUpdate"/>.
        /// </summary>
        public bool HasCatalogLine => !string.IsNullOrWhiteSpace(CatalogLine);
        public bool MissingIcon => string.IsNullOrWhiteSpace(IconUrl);
        public string Initials => string.IsNullOrWhiteSpace(DisplayName) ? "" : DisplayName.Substring(0, 1).ToUpperInvariant();

        private static readonly string[] _fallbackColors = {
            "#F44336", "#E91E63", "#9C27B0", "#673AB7", 
            "#3F51B5", "#2196F3", "#03A9F4", "#00BCD4", 
            "#009688", "#4CAF50", "#8BC34A", "#FF9800", 
            "#FF5722", "#795548", "#607D8B"
        };

        public string FallbackColor
        {
            get
            {
                var id = string.IsNullOrWhiteSpace(DisplayName) ? TizenId : DisplayName;
                int hash = 0;
                foreach (char c in id) hash = (hash * 31) + c;
                return _fallbackColors[System.Math.Abs(hash) % _fallbackColors.Length];
            }
        }

        /// <summary>Human-readable install size for this app ("—" when unknown/0).</summary>
        public string SizeDisplay => FormatSize(SizeBytes);

        /// <summary>The list row's second line: "v1.2.3 · 12.3 MB".</summary>
        public string Subline => string.IsNullOrWhiteSpace(Version) ? SizeDisplay : $"v{Version} · {SizeDisplay}";

        /// <summary>
        /// Formats a byte count as a human-readable size ("—" when 0 or negative, else one decimal
        /// KB/MB/GB, e.g. "12.6 MB"). Invariant so the decimal separator is always a dot. Shared for
        /// per-app sizes and for the per-TV total used space.
        /// </summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "—";

            const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
            var c = CultureInfo.InvariantCulture;
            if (bytes < mb) return string.Format(c, "{0:0.0} KB", bytes / kb);
            if (bytes < gb) return string.Format(c, "{0:0.0} MB", bytes / mb);
            return string.Format(c, "{0:0.0} GB", bytes / gb);
        }
    }
}
