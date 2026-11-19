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
            RestorePlacement();
            Closing += (_, _) => SavePlacement();
            KeyDown += OnShortcutKeyDown;
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

        /// <summary>
        /// Opens where it was closed (#1129). The size always; the position only while that spot is
        /// still on a screen, so a window left on a monitor that is gone opens centred as before.
        /// </summary>
        private void RestorePlacement()
        {
            var settings = Helpers.AppSettings.Default;
            if (settings.WindowWidth is { } width && settings.WindowHeight is { } height
                && width >= MinWidth && height >= MinHeight)
            {
                Width = width;
                Height = height;
            }
            if (settings.WindowX is { } x && settings.WindowY is { } y)
            {
                var corner = new Avalonia.PixelPoint(x, y);
                // The title bar has to be reachable: its left end, a little in from the corner.
                var grip = new Avalonia.PixelPoint(x + 40, y + 10);
                foreach (var screen in Screens.All)
                {
                    if (!screen.WorkingArea.Contains(grip))
                        continue;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = corner;
                    break;
                }
            }
            if (settings.WindowMaximized)
                WindowState = WindowState.Maximized;
        }

        private void SavePlacement()
        {
            var settings = Helpers.AppSettings.Default;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            // A maximized or minimized window keeps the size and place it had before that.
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
                settings.WindowX = Position.X;
                settings.WindowY = Position.Y;
            }
            settings.Save();
        }

        /// <summary>
        /// The two shortcuts every list app has (#1129): Ctrl+F (Cmd+F on a Mac) puts the cursor in
        /// the catalog's search box, F5 or Ctrl+R reads the current section again. Not on Remote,
        /// where the keyboard belongs to the TV.
        /// </summary>
        private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || DataContext is not MainWindowViewModel vm || vm.IsRemoteSection)
                return;
            var command = PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
            var withCommand = e.KeyModifiers == command;

            if (withCommand && e.Key == Key.F)
            {
                if (!vm.IsInstallSection)
                    vm.ShowSectionCommand.Execute("Install");
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if ((e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None) || (withCommand && e.Key == Key.R))
            {
                if (vm.IsOnTheTvSection && vm.InstalledAppsVm is { } apps)
                {
                    if (apps.LoadCommand.CanExecute(null))
                        apps.LoadCommand.Execute(null);
                    e.Handled = true;
                }
                else if (vm.IsInstallSection)
                {
                    if (vm.RefreshDevicesCommand.CanExecute(null))
                        vm.RefreshDevicesCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainWindowViewModel { IsRemoteSection: true, RemoteVm: { } remote })
                return;

            // Live typing: whatever is typed at the laptop goes to the TV, like the arrow keys already do.
            // Handled here (tunnel, before any focused control) so a text box elsewhere cannot swallow it.
            if (remote.LiveMode != RemoteViewModel.LiveKeyboardMode.Off
                && FocusManager?.GetFocusedElement() is not TextBox
                && (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta
                                      | Avalonia.Input.KeyModifiers.Alt)) == 0)
            {
                // Characters, Space, Backspace and Enter are typed on the TV. Shift is deliberately allowed
                // through `KeySymbol`, so capitals and punctuation like `?` arrive with it.
                var typed = e.Key switch
                {
                    Avalonia.Input.Key.Back => "\b",
                    Avalonia.Input.Key.Enter => "\r",
                    Avalonia.Input.Key.Space => " ",
                    _ => null,
                };

                if (typed is null)
                {
                    var text = e.KeySymbol;
                    if (!string.IsNullOrEmpty(text) && text.Length == 1 && !char.IsControl(text[0]))
                        typed = text;
                }

                if (typed is not null)
                {
                    e.Handled = true;
                    await remote.SendLiveKeyAsync(typed);
                    return;
                }

                // Anything else — arrows, Home/Menu/Back on the remote, Escape — is not typing, so it falls
                // through to the normal handler below and keeps driving the TV.
            }

            await RemoteView.HandleKeyAsync(remote, e, FocusManager?.GetFocusedElement());
        }

    }
}
