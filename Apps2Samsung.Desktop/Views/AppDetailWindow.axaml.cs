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

        // The developer's name: nothing to install, the main window searches the catalog for them (#888).
        private void OnAuthorClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AppDetailViewModel vm)
                vm.SearchAuthorRequested = true;
            Close(null);
        }
    }
}
