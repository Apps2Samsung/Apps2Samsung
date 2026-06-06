using Apps2Samsung.Models;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Represents the user's choice in the update dialog.
    /// </summary>
    public enum UpdateDialogChoice
    {
        /// <summary>
        /// User cancelled or closed the dialog.
        /// </summary>
        Cancel,

        /// <summary>
        /// User chose to open the releases page manually.
        /// </summary>
        Manual,

        /// <summary>
        /// User chose to download and install the update automatically.
        /// </summary>
        Automatic,

        /// <summary>
        /// User chose to skip this update.
        /// </summary>
        Skip
    }

    /// <summary>
    /// Service for showing update-related dialogs.
    /// </summary>
    public interface IUpdateDialogService
    {
        /// <summary>
        /// Shows the update available dialog with options for manual or automatic update.
        /// </summary>
        /// <param name="updateInfo">Information about the available update.</param>
        /// <returns>The user's choice.</returns>
        Task<UpdateDialogChoice> ShowUpdateAvailableDialogAsync(UpdateCheckResult updateInfo);

        /// <summary>
        /// Shows a progress dialog while downloading the update.
        /// </summary>
        /// <param name="progress">Progress reporter (0-100).</param>
        /// <returns>True if download completed, false if cancelled.</returns>
        Task<bool> ShowDownloadProgressAsync(System.IProgress<int> progress);

        /// <summary>
        /// Shows a message that the update is being applied and the app will restart.
        /// </summary>
        Task ShowApplyingUpdateMessageAsync();

        /// <summary>
        /// Shows an error message related to the update process.
        /// </summary>
        /// <param name="errorMessage">The error message to display.</param>
        Task ShowUpdateErrorAsync(string errorMessage);
    }
}
