using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    /// <summary>Shows an <see cref="AppDetailViewModel"/>; closes with the chosen asset on Install,
    /// null otherwise.</summary>
    public partial class AppDetailWindow : Window
    {
        public AppDetailWindow()
        {
            InitializeComponent();
        }

        public AppDetailWindow(AppDetailViewModel viewModel) : this()
        {
            DataContext = viewModel;
            Opened += async (_, _) => await viewModel.LoadPreviewAsync();
        }

        private void OnInstallClick(object? sender, RoutedEventArgs e)
        {
            Close((DataContext as AppDetailViewModel)?.SelectedAsset);
        }

        // Another source of the same app (#1018): the main window opens that entry's sheet.
        private void OnOtherSourceClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AppDetailViewModel vm && sender is Button { Tag: Apps2Samsung.Models.GitHubRelease other })
                vm.SwitchTo = other;
            Close(null);
        }

        // ⋯ > Copy install link (#1066): the https form, which works in a README too.
        private void OnCopyLinkClick(object? sender, RoutedEventArgs e) =>
            CopyLink((DataContext as AppDetailViewModel)?.InstallLinkUrl);

        private void OnCopyVersionLinkClick(object? sender, RoutedEventArgs e) =>
            CopyLink((DataContext as AppDetailViewModel)?.VersionLinkUrl);

        private async void CopyLink(string? link)
        {
            if (link is null || DataContext is not AppDetailViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                return;
            await clipboard.SetTextAsync(link);
            vm.CopiedLink = link;
            await System.Threading.Tasks.Task.Delay(4000);
            if (vm.CopiedLink == link)
                vm.CopiedLink = string.Empty;
        }

        // The developer's name: nothing to install, the main window searches the catalog for them (#888).
        private void OnAuthorClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AppDetailViewModel vm)
                vm.SearchAuthorRequested = true;
            Close(null);
        }
    }
}
