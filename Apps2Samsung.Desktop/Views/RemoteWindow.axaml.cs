using Apps2Samsung.Remote;
using Apps2Samsung.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Generic;

namespace Apps2Samsung.Views
{
    public partial class RemoteWindow : Window
    {
        // Physical keyboard as the remote: the keys anyone would reach for without being told.
        private static readonly Dictionary<Key, string> KeyboardMap = new()
        {
            [Key.Up] = SamsungRemoteKeys.Up,
            [Key.Down] = SamsungRemoteKeys.Down,
            [Key.Left] = SamsungRemoteKeys.Left,
            [Key.Right] = SamsungRemoteKeys.Right,
            [Key.Enter] = SamsungRemoteKeys.Enter,
            [Key.Back] = SamsungRemoteKeys.Back,
            [Key.Escape] = SamsungRemoteKeys.Back,
            [Key.Home] = SamsungRemoteKeys.Home,
            [Key.M] = SamsungRemoteKeys.Mute,
            [Key.Add] = SamsungRemoteKeys.VolumeUp,
            [Key.OemPlus] = SamsungRemoteKeys.VolumeUp,
            [Key.Subtract] = SamsungRemoteKeys.VolumeDown,
            [Key.OemMinus] = SamsungRemoteKeys.VolumeDown,
        };

        public RemoteWindow(RemoteViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.OnRequestClose += Close;
            // Connect once the window is up, so the pairing prompt and any wake happen in view.
            Opened += async (_, _) => await vm.ConnectCommand.ExecuteAsync(null);
            // Drop the channel with the window; the TV keeps the pairing, so reopening is silent.
            Closed += async (_, _) => await vm.DisposeAsync();
        }

        // Parameterless ctor for the XAML designer.
        public RemoteWindow() => InitializeComponent();

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            // Don't steal keystrokes meant for the text field.
            if (DataContext is not RemoteViewModel vm || FocusManager?.GetFocusedElement() is TextBox)
            {
                base.OnKeyDown(e);
                return;
            }

            if (e.Key == Key.Space)
            {
                e.Handled = true;
                await vm.PlayPauseCommand.ExecuteAsync(null);
                return;
            }

            if (KeyboardMap.TryGetValue(e.Key, out var remoteKey))
            {
                e.Handled = true;
                await vm.SendKeyCommand.ExecuteAsync(remoteKey);
                return;
            }

            base.OnKeyDown(e);
        }
    }
}
