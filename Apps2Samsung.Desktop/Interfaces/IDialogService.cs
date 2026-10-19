using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface IDialogService
    {
        Task ShowMessageAsync(string title, string message);
        Task ShowErrorAsync(string message);
        /// <summary>Confirmation prompt. Null button labels fall back to the localized Yes/No.</summary>
        Task<bool> ShowConfirmationAsync(string title, string message, string? yes = null, string? no = null, Window? owner = null);
        Task<string?> PromptForIpAsync();

        /// <summary>
        /// A message dialog whose primary button opens <paramref name="url"/> in the system browser
        /// before closing. Used where the fix lives on a web page the user has to visit (e.g. adding
        /// a missing email to their Samsung account).
        /// </summary>
        Task ShowMessageWithLinkAsync(string title, string message, string linkText, string url, string? closeText = null);

        /// <summary>Prompts for a free-form line of text. Returns the entered text, or null if cancelled.</summary>
        Task<string?> PromptForTextAsync(string title, string message, string placeholder);

        /// <summary>
        /// Holds an install that can't proceed yet because the signing certificate's validity period
        /// hasn't started, counting down to <paramref name="validFromLocal"/>. Returns true when the
        /// wait is over and the user chose to continue, false if they cancelled.
        /// </summary>
        Task<bool> ShowCertificateCountdownAsync(DateTime validFromLocal);

        /// <summary>
        /// Opens the certificate sheet (not awaited: it stays open while the certificate is made)
        /// for <paramref name="tvName"/>. Null when there is no window to put it on.
        /// </summary>
        Task<Apps2Samsung.Services.CertificateSheet?> OpenCertificateSheetAsync(string tvName, Apps2Samsung.Interfaces.CertificatePrivilegeLevel level);

        /// <summary>The Samsung account has no email address: what to do, and the account page.</summary>
        Task ShowAccountEmailMissingAsync();

        /// <summary>
        /// The Developer Mode guide for <paramref name="device"/>, re-read through
        /// <paramref name="refresh"/> while it is open. From Install (<paramref name="fromInstall"/>)
        /// it returns whether to go on; otherwise false.
        /// </summary>
        Task<bool> ShowDevModeGuideAsync(
            Apps2Samsung.Models.NetworkDevice device,
            string tvName,
            Func<Task> refresh,
            System.Collections.Generic.IReadOnlyCollection<string> localIps,
            string? localIp,
            bool fromInstall);

        /// <summary>The debug log viewer.</summary>
        Task ShowLogViewerAsync();
    }
}
