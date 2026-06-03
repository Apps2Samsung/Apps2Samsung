using System;

namespace Jellyfin2Samsung.Models
{
    /// <summary>
    /// Result of checking for application updates.
    /// </summary>
    public class UpdateCheckResult
    {
        /// <summary>
        /// Whether a newer version is available.
        /// </summary>
        public bool IsUpdateAvailable { get; set; }

        /// <summary>
        /// The current installed version.
        /// </summary>
        public string CurrentVersion { get; set; } = string.Empty;

        /// <summary>
        /// The latest available version.
        /// </summary>
        public string LatestVersion { get; set; } = string.Empty;

        /// <summary>
        /// URL to download the update for the current platform.
        /// </summary>
        public string? DownloadUrl { get; set; }

        /// <summary>
        /// URL to the GitHub releases page.
        /// </summary>
        public string ReleasesPageUrl { get; set; } = string.Empty;

        /// <summary>
        /// Release title/name.
        /// </summary>
        public string ReleaseTitle { get; set; } = string.Empty;

        /// <summary>
        /// Release notes or description.
        /// </summary>
        public string ReleaseNotes { get; set; } = string.Empty;

        /// <summary>
        /// When the release was published.
        /// </summary>
        public DateTime? PublishedAt { get; set; }

        /// <summary>
        /// Whether an automatic (in-place) update can be applied for this installation.
        /// False when the app is managed by an OS installer/package manager
        /// (MSI, .deb, macOS .app bundle), where the user should update via that channel instead.
        /// </summary>
        public bool SupportsAutomaticUpdate { get; set; } = true;

        /// <summary>
        /// Error message if the check failed.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Whether the check completed successfully.
        /// </summary>
        public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);

        /// <summary>
        /// Creates a failed result with an error message.
        /// </summary>
        public static UpdateCheckResult Failed(string errorMessage, string currentVersion)
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVersion,
                ErrorMessage = errorMessage
            };
        }

        /// <summary>
        /// Creates a result indicating no update is available.
        /// </summary>
        public static UpdateCheckResult NoUpdateAvailable(string currentVersion)
        {
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion
            };
        }
    }
}
