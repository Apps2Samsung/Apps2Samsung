using Avalonia.Controls;
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
    }
}
