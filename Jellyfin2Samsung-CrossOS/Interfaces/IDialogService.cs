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

    }
}
