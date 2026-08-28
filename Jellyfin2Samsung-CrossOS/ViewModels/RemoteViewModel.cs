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
        /// when the window opens, and again by the reconnect button.
        /// </summary>
        [RelayCommand]
        private async Task Connect()
        {
            IsBusy = true;
            try
            {
                StatusText = "lblRemoteConnecting".Localized();

                var capability = await SamsungRemoteClient.ProbeAsync(_tvIp);

                // A sleeping TV serves neither the REST API nor the remote channel, so "no answer"
                // and "standby" are one situation: nothing works until the set is woken.
                if (!capability.Supported || !capability.IsAwake)
                {
                    capability = await TryWakeAsync(capability);
                    if (!capability.Supported || !capability.IsAwake)
                        return;
                }

                // Remember the MAC while it is readable — a sleeping TV won't tell us later.
                if (!string.IsNullOrEmpty(capability.MacAddress))
                    RemoteStore.SetMac(_tvIp, capability.MacAddress);

                var stored = RemoteStore.GetToken(_tvIp);
                var client = new SamsungRemoteClient(_tvIp, token: stored, secure: capability.UsesToken);
                client.TokenIssued += token => RemoteStore.SetToken(_tvIp, token);

                var firstPairing = capability.UsesToken && string.IsNullOrEmpty(stored);
                if (firstPairing)
                    StatusText = "lblRemotePairPrompt".Localized();

                // A first pairing needs someone to walk to the TV and accept the prompt.
                using var cts = new CancellationTokenSource(firstPairing
                    ? TimeSpan.FromSeconds(60)
                    : TimeSpan.FromSeconds(10));

                if (!await client.ConnectAsync(cts.Token))
                {
                    await client.DisposeAsync();
                    StatusText = firstPairing
                        ? "lblRemotePairFailed".Localized()
                        : "lblRemoteNoChannel".Localized();
                    return;
                }

                _remote = client;
                IsConnected = true;
                StatusText = "lblRemoteConnected".Localized();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task<SamsungRemoteCapability> TryWakeAsync(SamsungRemoteCapability capability)
        {
            var mac = RemoteStore.GetMac(_tvIp);
            if (string.IsNullOrEmpty(mac))
            {
                // Never seen this TV awake, so there is no MAC to wake it with.
                StatusText = "lblRemoteWakeNoMac".Localized();
                return capability;
            }

            StatusText = "lblRemoteWaking".Localized();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (await SamsungRemoteWake.WakeAndWaitAsync(_tvIp, mac, TimeSpan.FromSeconds(40), cts.Token))
                return await SamsungRemoteClient.ProbeAsync(_tvIp);

            // Wake-on-LAN needs the TV's own network-standby setting on, and a LAN that passes broadcast.
            StatusText = "lblRemoteWakeFailed".Localized();
            return capability;
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

            if (await remote.SendTextAsync(text))
            {
                TextToSend = string.Empty;
                StatusText = "lblRemoteTextSent".Localized();
            }
            else
            {
                StatusText = "lblRemoteTextFailed".Localized();
            }
        }

        private async Task<bool> PressAsync(string key)
        {
            var remote = _remote;
            if (remote is null)
            {
                StatusText = "lblRemoteNotConnected".Localized();
                return false;
            }

            if (await remote.SendKeyAsync(key))
                return true;

            // The client reconnects on its own, so a single miss is worth reporting but not fatal.
            IsConnected = remote.IsConnected;
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
