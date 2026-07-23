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
        bool IsRemovable)
    {
        /// <summary>Title if the TV reported one, otherwise the Tizen id — never empty.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? TizenId : Title;
    }
}
