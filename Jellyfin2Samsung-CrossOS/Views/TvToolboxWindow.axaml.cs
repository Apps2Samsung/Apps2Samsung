using Apps2Samsung.ViewModels;
using Avalonia.Controls;

namespace Apps2Samsung.Views
{
    public partial class TvToolboxWindow : Window
    {
        public TvToolboxWindow(TvToolboxViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.OnRequestClose += Close;
            // Load once the window is up, so the app list, the pairing prompt and any wake happen in view.
            Opened += async (_, _) => await vm.LoadCommand.ExecuteAsync(null);
            // Drop the channel with the window; the TV keeps the pairing, so reopening is silent.
            Closed += async (_, _) => await vm.DisposeAsync();
        }

        // Parameterless ctor for the XAML designer.
        public TvToolboxWindow() => InitializeComponent();
    }
}
