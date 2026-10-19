using Apps2Samsung.Remote;
using Apps2Samsung.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using System.Collections.Generic;

namespace Apps2Samsung.Views
{
    /// <summary>Hosts a <see cref="RemoteViewModel"/>: connects when the section is shown for a
    /// view model that isn't connected yet, and maps the physical keyboard onto the remote.</summary>
    public partial class RemoteView : UserControl
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
            [Key.D0] = SamsungRemoteKeys.Digit(0), [Key.NumPad0] = SamsungRemoteKeys.Digit(0),
            [Key.D1] = SamsungRemoteKeys.Digit(1), [Key.NumPad1] = SamsungRemoteKeys.Digit(1),
            [Key.D2] = SamsungRemoteKeys.Digit(2), [Key.NumPad2] = SamsungRemoteKeys.Digit(2),
            [Key.D3] = SamsungRemoteKeys.Digit(3), [Key.NumPad3] = SamsungRemoteKeys.Digit(3),
            [Key.D4] = SamsungRemoteKeys.Digit(4), [Key.NumPad4] = SamsungRemoteKeys.Digit(4),
            [Key.D5] = SamsungRemoteKeys.Digit(5), [Key.NumPad5] = SamsungRemoteKeys.Digit(5),
            [Key.D6] = SamsungRemoteKeys.Digit(6), [Key.NumPad6] = SamsungRemoteKeys.Digit(6),
            [Key.D7] = SamsungRemoteKeys.Digit(7), [Key.NumPad7] = SamsungRemoteKeys.Digit(7),
            [Key.D8] = SamsungRemoteKeys.Digit(8), [Key.NumPad8] = SamsungRemoteKeys.Digit(8),
            [Key.D9] = SamsungRemoteKeys.Digit(9), [Key.NumPad9] = SamsungRemoteKeys.Digit(9),
        };

        private RemoteViewModel? _connected;

        public RemoteView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Hook();
            AttachedToVisualTree += (_, _) => Hook();
        }

        private async void Hook()
        {
            if (DataContext is not RemoteViewModel vm || ReferenceEquals(vm, _connected) || this.GetVisualRoot() is null)
                return;
            _connected = vm;
            Focus();
            await vm.ConnectCommand.ExecuteAsync(null);
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            // Don't steal keystrokes meant for the text field.
            if (DataContext is not RemoteViewModel vm || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
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
