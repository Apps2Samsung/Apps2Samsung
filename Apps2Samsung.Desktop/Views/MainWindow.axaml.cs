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
