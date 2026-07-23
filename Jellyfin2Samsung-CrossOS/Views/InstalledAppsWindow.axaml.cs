using Avalonia.Controls;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    public partial class InstalledAppsWindow : Window
    {
        public InstalledAppsWindow(InstalledAppsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.OnRequestClose += Close;
            // Kick off the initial load once the window is shown.
            Opened += async (_, _) => await vm.LoadCommand.ExecuteAsync(null);
        }

        // Parameterless ctor for the XAML designer.
        public InstalledAppsWindow() => InitializeComponent();
    }
}
