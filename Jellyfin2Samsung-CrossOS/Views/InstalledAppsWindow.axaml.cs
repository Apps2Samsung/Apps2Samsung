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
            // This window is modal over the main window, so the console is owned by it: it stays on
            // top of the list and goes away with it.
            vm.OnRequestDebugConsole += console => new DebugConsoleWindow(console).Show(this);
            // Kick off the initial load once the window is shown.
            Opened += async (_, _) => await vm.LoadCommand.ExecuteAsync(null);
            Closed += (s, e) => {
                if (vm is System.IDisposable d) d.Dispose();
            };
        }

        // Parameterless ctor for the XAML designer.
        public InstalledAppsWindow() => InitializeComponent();
    }
}
