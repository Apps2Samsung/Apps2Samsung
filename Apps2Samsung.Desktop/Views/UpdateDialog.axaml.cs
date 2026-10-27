using Avalonia.Controls;
using Avalonia.Styling;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Apps2Samsung.Views;

public partial class UpdateDialog : Window
{
    public UpdateDialogViewModel ViewModel { get; }

    public UpdateDialog()
    {
        InitializeComponent();

        var localizationService = App.Services.GetRequiredService<ILocalizationService>();
        ViewModel = new UpdateDialogViewModel(localizationService);
        DataContext = ViewModel;

        // Apply theme
        RequestedThemeVariant = AppSettings.Default.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;

        // Handle close request from ViewModel
        ViewModel.RequestClose += (_, _) => Close();
    }

    public void Initialize(UpdateCheckResult updateInfo)
    {
        ViewModel.Initialize(updateInfo);
    }

    public async Task<UpdateDialogChoice> ShowDialogAsync(Window parent)
    {
        await ShowDialog(parent);
        return ViewModel.DialogResult ?? UpdateDialogChoice.Cancel;
    }

    public void UpdateProgress(int progress, string status)
    {
        ViewModel.UpdateDownloadProgress(progress, status);
    }

    public void SetDownloading(bool isDownloading)
    {
        ViewModel.IsDownloading = isDownloading;
    }
}
