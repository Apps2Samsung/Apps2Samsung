using Jellyfin2Samsung.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin2Samsung.Interfaces
{
    /// <summary>
    /// Service for checking and applying application updates via GitHub releases.
    /// Uses the Atom feed endpoint to avoid API rate limiting.
    /// </summary>
    public interface IUpdaterService
    {
        /// <summary>
        /// Checks if a newer version of the application is available.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>Update check result containing version information and download URLs.</returns>
        Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads the latest release to a temporary location.
        /// </summary>
        /// <param name="downloadUrl">The URL to download the release from.</param>
        /// <param name="progress">Progress callback reporting download percentage (0-100).</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>Path to the downloaded file.</returns>
        Task<string> DownloadUpdateAsync(
            string downloadUrl,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Applies the downloaded update by extracting, replacing files, and scheduling a restart.
        /// </summary>
        /// <param name="downloadedFilePath">Path to the downloaded update archive.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>True if the update was successfully prepared and app should restart.</returns>
        Task<bool> ApplyUpdateAsync(string downloadedFilePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens the GitHub releases page in the default browser.
        /// </summary>
        void OpenReleasesPage();

        /// <summary>
        /// Determines whether automatic (in-place) updates are supported for the current installation.
        /// Returns false when the app runs from a location managed by an OS installer
        /// (e.g. an MSI under Program Files, a .deb under /usr or /opt, or a macOS .app bundle),
        /// where overwriting files would require elevation or conflict with the package manager.
        /// </summary>
        bool IsAutomaticUpdateSupported();

        /// <summary>
        /// Gets the URL of the GitHub releases page.
        /// </summary>
        string ReleasesPageUrl { get; }

        /// <summary>
        /// Gets the current application version.
        /// </summary>
        string CurrentVersion { get; }
    }
}
