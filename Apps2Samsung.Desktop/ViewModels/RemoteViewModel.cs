using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Drives the desktop remote over the shared Core client (#544) — the same
    /// <see cref="SamsungRemoteClient"/> the mobile head uses, so the protocol, pairing and wake
    /// behaviour are identical on both heads and only the UI differs.
    /// </summary>
    public partial class RemoteViewModel : ViewModelBase, IAsyncDisposable
    {
        private readonly string _tvIp;
        private SamsungRemoteClient? _remote;
        // Play/pause: newer sets take the single toggle, older ones only the separate keys. Once the
        // toggle is refused we stop trying it and alternate Play/Pause for the rest of the session.
        private bool _toggleUnsupported;
        private bool _lastWasPlay;

        public string TvLabel { get; }

        [ObservableProperty]
        private string statusText = string.Empty;

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private string textToSend = string.Empty;

        /// <summary>
        /// Type by walking the app's own on-screen keyboard with the d-pad instead of sending
        /// <c>SendInputString</c>. Off by default because it is slower and it needs the app's keyboard
        /// on screen, but it is the only way into apps that draw their own — YouTube and everything
        /// built on it, TizenTube included — whose grids take nothing from the TV's IME. The layout
        /// is YouTube's alphabetical grid, so other apps would need one of their own; see
        /// <see cref="SamsungRemoteKeyboard"/> for why.
        /// </summary>
        [ObservableProperty]
        private bool typeOnAppKeyboard;

        /// <summary>The dot beside the status line once nothing is in flight: green with the channel
        /// up, amber when a press or a text didn't get through, red when there is no channel.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusWarn), nameof(IsStatusBad))]
        private StatusTone tone = StatusTone.Bad;

        public enum StatusTone { Ok, Warn, Bad }

        public bool IsStatusOk => Tone == StatusTone.Ok;
        public bool IsStatusWarn => Tone == StatusTone.Warn;
        public bool IsStatusBad => Tone == StatusTone.Bad;

        public event Action? OnRequestClose;

        public RemoteViewModel(string tvIp, string tvLabel)
        {
            _tvIp = tvIp;
            TvLabel = tvLabel;
        }

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();

        /// <summary>
        /// Opens the channel, waking the TV first when it isn't answering and we know its MAC. Called
        /// when the window opens, and again by the reconnect button. The sequence itself lives in
        /// Core (<see cref="RemoteSession"/>) — the TV toolbox needs the same one.
        /// </summary>
        [RelayCommand]
        private async Task Connect()
        {
            IsBusy = true;
            try
            {
                var progress = new Progress<string>(key => StatusText = key.Localized());
                var session = await RemoteSession.ConnectAsync(_tvIp, RemoteStore.Credentials.Instance, progress);

                if (!session.Connected)
                {
                    Tone = StatusTone.Bad;
                    StatusText = RemoteStore.StatusKeyFor(session.Outcome).Localized();
                    return;
                }

                _remote = session.Client;
                IsConnected = true;
                Tone = StatusTone.Ok;
                StatusText = "lblRemoteConnected".Localized();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Wake: a set in standby serves no remote channel, so the way back is a fresh connect, which
        /// sends the magic packet when the probe finds the TV asleep (<see cref="RemoteSession"/>).
        /// Also the reconnect after a refused pairing or a dropped link. With the channel up the TV is
        /// awake already and there is nothing to do.
        /// </summary>
        [RelayCommand]
        private async Task Wake()
        {
            if (IsBusy)
                return;
            if (_remote is { IsConnected: true })
            {
                IsConnected = true;
                Tone = StatusTone.Ok;
                StatusText = "lblRemoteConnected".Localized();
                return;
            }

            var stale = _remote;
            _remote = null;
            IsConnected = false;
            if (stale is not null)
                await stale.DisposeAsync();
            await Connect();
        }

        /// <summary>
        /// The one power key (#755, option C): with the channel up it switches the TV off, without one
        /// it is Wake, the way a sleeping TV comes back.
        /// </summary>
        [RelayCommand]
        private async Task PowerOrWake()
        {
            if (IsConnected && _remote is { IsConnected: true })
                await PressAsync(SamsungRemoteKeys.Power);
            else
                await Wake();
        }

        /// <summary>Sends one key code (the buttons pass it as the command parameter).</summary>
        [RelayCommand]
        private async Task SendKey(string? key)
        {
            if (!string.IsNullOrEmpty(key))
                await PressAsync(key);
        }

        [RelayCommand]
        private async Task PlayPause()
        {
            if (!_toggleUnsupported)
            {
                if (await PressAsync(SamsungRemoteKeys.PlayPause))
                    return;
                _toggleUnsupported = true;
            }

            _lastWasPlay = !_lastWasPlay;
            await PressAsync(_lastWasPlay ? SamsungRemoteKeys.Play : SamsungRemoteKeys.Pause);
        }

        [RelayCommand]
        private async Task SendText()
        {
            var text = TextToSend;
            if (string.IsNullOrEmpty(text))
                return;

            var remote = _remote;
            if (remote is null)
            {
                StatusText = "lblRemoteNotConnected".Localized();
                return;
            }

            if (TypeOnAppKeyboard)
            {
                await TypeOnAppKeyboardAsync(remote, text);
                return;
            }

            if (await remote.SendTextAsync(text))
            {
                TextToSend = string.Empty;
                Tone = StatusTone.Ok;
                StatusText = "lblRemoteTextSent".Localized();
            }
            else
            {
                Tone = StatusTone.Warn;
                StatusText = "lblRemoteTextFailed".Localized();
            }
        }

        /// <summary>
        /// Types <paramref name="text"/> by pressing the keys the app's own on-screen keyboard
        /// understands. The plan parks the cursor on the grid's first cell before typing, so it does
        /// not matter where the app left it — but it does need that keyboard to be open and in front,
        /// which is the caller's job and the honest caveat in the UI.
        /// </summary>
        private async Task TypeOnAppKeyboardAsync(SamsungRemoteClient remote, string text)
        {
            var plan = SamsungRemoteKeyboard.YouTubeSearch.PlanFromUnknownCursor(text);
            if (plan.Keys.Count == 0)
            {
                Tone = StatusTone.Warn;
                StatusText = string.Format("lblRemoteKeyboardUnsupported".Localized(), text);
                return;
            }

            IsBusy = true;
            try
            {
                if (await remote.SendKeysAsync(plan.Keys))
                {
                    Tone = StatusTone.Ok;
                    // A grid that cannot spell the whole string types what it can; say so rather than
                    // claiming the lot went in, and keep the box so the rest can be fixed up by hand.
                    if (plan.Unsupported is null)
                    {
                        TextToSend = string.Empty;
                        StatusText = "lblRemoteKeyboardTyped".Localized();
                    }
                    else
                    {
                        StatusText = string.Format("lblRemoteKeyboardUnsupported".Localized(), plan.Unsupported);
                    }
                }
                else
                {
                    IsConnected = remote.IsConnected;
                    Tone = StatusTone.Warn;
                    StatusText = "lblRemoteKeyFailed".Localized();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<bool> PressAsync(string key)
        {
            var remote = _remote;
            if (remote is null)
            {
                Tone = StatusTone.Bad;
                StatusText = "lblRemoteNotConnected".Localized();
                return false;
            }

            if (await remote.SendKeyAsync(key))
                return true;

            // The client reconnects on its own, so a single miss is worth reporting but not fatal.
            IsConnected = remote.IsConnected;
            Tone = IsConnected ? StatusTone.Warn : StatusTone.Bad;
            StatusText = "lblRemoteKeyFailed".Localized();
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            var remote = _remote;
            _remote = null;
            IsConnected = false;
            if (remote is not null)
                await remote.DisposeAsync();
        }
    }
}
