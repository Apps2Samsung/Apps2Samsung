using System.Globalization;

namespace Apps2Samsung.Models
{
    /// <summary>
    /// One app reported installed on a TV by the <c>vd_applist</c> query, parsed by
    /// <see cref="Apps2Samsung.Sdb.TizenInstalledApps"/>. Shared by both heads' "installed apps" view.
    /// </summary>
    public sealed record InstalledApp(
        string Title,
        string TizenId,
        string Version,
        string InstallDate,
        bool IsRemovable,
        long SizeBytes = 0)
    {
        /// <summary>Title if the TV reported one, otherwise the Tizen id — never empty.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? TizenId : Title;

        /// <summary>Human-readable install size for this app ("—" when unknown/0).</summary>
        public string SizeDisplay => FormatSize(SizeBytes);

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
