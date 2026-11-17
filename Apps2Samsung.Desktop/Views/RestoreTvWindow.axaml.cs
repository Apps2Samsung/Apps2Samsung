using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    /// <summary>The restore sheet (#1069). Closes with true when the user chose Restore, false otherwise.</summary>
    public partial class RestoreTvWindow : Window
    {
        public RestoreTvWindow()
        {
            InitializeComponent();
        }

        public RestoreTvWindow(RestoreTvViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }

        private void OnRestoreClick(object? sender, RoutedEventArgs e) => Close(true);

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    }
}
