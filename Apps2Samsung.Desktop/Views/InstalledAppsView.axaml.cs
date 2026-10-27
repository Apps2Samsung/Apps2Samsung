using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    /// <summary>Hosts an <see cref="InstalledAppsViewModel"/>: loads the list when the section is
    /// shown for a view model it hasn't loaded yet, opens the debug console as a window owned by
    /// the main window, and opens a row's menu from its ⋯.</summary>
    public partial class InstalledAppsView : UserControl
    {
        private InstalledAppsViewModel? _loaded;

        public InstalledAppsView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Hook();
            AttachedToVisualTree += (_, _) => Hook();
        }

        private async void Hook()
        {
            if (DataContext is not InstalledAppsViewModel vm || ReferenceEquals(vm, _loaded) || this.GetVisualRoot() is null)
                return;
            _loaded = vm;
            vm.OnRequestDebugConsole += console =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                    new DebugConsoleWindow(console).Show(owner);
            };
            await vm.LoadCommand.ExecuteAsync(null);
        }

        // A row's ⋯ opens the same menu as a right-click on the row.
        private void OnMoreClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Control button && button.Parent is Control row)
                row.ContextFlyout?.ShowAt(button);
        }
    }
}
