using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Apps2Samsung.ViewModels;

namespace Apps2Samsung.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            this.Opened += async (_, __) =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.InitializeAsync();
                    // If init was cancelled (e.g. by the update check) the release
                    // list can be left empty — recover once init has settled.
                    await vm.EnsureReleasesLoadedAsync();
                }
            };

            // Recover an empty release list when the window regains focus, so the
            // user doesn't have to restart the app. No-op once releases are loaded.
            this.Activated += async (_, __) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    await vm.EnsureReleasesLoadedAsync();
            };

            // While the Remote section is up the keyboard is the remote: tunnel, so the arrows and
            // Enter reach the TV before a focused sidebar or toolbar button takes them.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        }

        private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel { IsRemoteSection: true, RemoteVm: { } remote })
                return;
            await RemoteView.HandleKeyAsync(remote, e, FocusManager?.GetFocusedElement());
        }
    }
}
