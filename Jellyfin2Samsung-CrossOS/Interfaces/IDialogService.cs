using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface IDialogService
    {
        Task ShowMessageAsync(string title, string message);
        Task ShowErrorAsync(string message);
        Task<bool> ShowConfirmationAsync(string title, string message, string yes, string no, Window? owner = null);
        Task<string?> PromptForIpAsync();

        /// <summary>Prompts for a free-form line of text. Returns the entered text, or null if cancelled.</summary>
        Task<string?> PromptForTextAsync(string title, string message, string placeholder);

        /// <summary>
        /// Holds an install that can't proceed yet because the signing certificate's validity period
        /// hasn't started, counting down to <paramref name="validFromLocal"/>. Returns true when the
        /// wait is over and the user chose to continue, false if they cancelled.
        /// </summary>
        Task<bool> ShowCertificateCountdownAsync(string title, string message, DateTime validFromLocal);
    }
}
