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
        Task<Apps2Samsung.Services.CertificateSheet?> OpenCertificateSheetAsync(string tvName, Apps2Samsung.Interfaces.CertificatePrivilegeLevel level, string? tvDuid = null);

        /// <summary>
        /// The Tizen certificate sheet (#988), not awaited either: two steps and no sign-in. Null
        /// <paramref name="tvName"/> for one made from Settings with no TV in mind.
        /// </summary>
        Task<Apps2Samsung.Services.CertificateSheet?> OpenTizenCertificateSheetAsync(string? tvName, Apps2Samsung.Certificate.TizenDistributorLevel level);

        /// <summary>
        /// The TV refused the Tizen certificate (#988): the sheet with the TV's words and Sign in with
        /// Samsung instead. True to switch to a Samsung certificate, false to keep the setting.
        /// </summary>
        Task<bool> ShowTizenCertificateRefusedAsync(string tvName, Apps2Samsung.Certificate.TizenDistributorLevel level, string profileName, string? reason, DateTime? validUntil);

        /// <summary>The Samsung account has no email address: what to do, and the account page.</summary>
        Task ShowAccountEmailMissingAsync();

        /// <summary>Settings > Contribute > Add an app: the package fields, then a pull request or the issue form.</summary>
        Task ShowContributeAppAsync();

        /// <summary>Add a source of your own (a repository, release or file link); null when cancelled.</summary>
        Task<Apps2Samsung.Catalog.UserSource?> ShowAddUserSourceAsync(System.Collections.Generic.IReadOnlyList<Apps2Samsung.Catalog.UserSource> existing);
        /// <summary>A signed repository whose key changed (#1018): its new key, to trust or not; the source to store instead, or null.</summary>
        Task<Apps2Samsung.Catalog.UserSource?> ShowReviewRepositoryKeyAsync(Apps2Samsung.Catalog.UserSource source);

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

        /// <summary>
        /// The form for a community package's <c>user_settings</c> (#791), prefilled with
        /// <paramref name="current"/>. The values by key once the user chose Install, null on Cancel.
        /// </summary>
        Task<System.Collections.Generic.IReadOnlyDictionary<string, string>?> ShowUserSettingsAsync(
            string appName,
            Apps2Samsung.Models.UserSettingsSpec spec,
            System.Collections.Generic.IReadOnlyDictionary<string, string> current);
    }
}
