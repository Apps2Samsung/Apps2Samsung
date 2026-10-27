using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Contract for an app-specific settings section shown in the Settings window's
    /// left-hand list (e.g. Jellyfin, and future apps). The generic "Application"
    /// section is not a provider; only per-app settings implement this.
    /// Register implementations in DI; the Settings window discovers them via
    /// <c>IEnumerable&lt;IAppSettingsProvider&gt;</c>.
    /// </summary>
    public interface IAppSettingsProvider
    {
        /// <summary>Stable identifier for the app, e.g. "jellyfin".</summary>
        string ProviderId { get; }

        /// <summary>Display name shown in the section list, e.g. "Jellyfin".</summary>
        string DisplayName { get; }

        /// <summary>Ordering within the list (lower comes first).</summary>
        int SortOrder { get; }

        /// <summary>
        /// Creates (or resolves) the view model backing this app's settings view.
        /// The view is located by convention via <c>ViewLocator</c>.
        /// </summary>
        ViewModelBase CreateSettingsViewModel();
    }
}
