using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

        // ---- TV toolbox (#635) ----

        /// <summary>The combinations this channel can deliver — the standby ones are not offered.</summary>
        public IReadOnlyList<ToolboxSequence> Sequences { get; }

        /// <summary>The apps that can be tried by id: what the TV reports, plus the community catalogue.</summary>
        public ObservableCollection<ToolboxApp> Apps { get; } = new();

        /// <summary>Gap between the presses of a sequence, in milliseconds. The slider's value.</summary>
        [ObservableProperty]
        private double gapMs = SamsungRemoteSequences.DefaultGapMs;

        [ObservableProperty]
        private string toolboxStatus = string.Empty;

        [ObservableProperty]
        private bool isToolboxBusy;

        /// <summary>Id typed by hand, for an app in neither list.</summary>
        [ObservableProperty]
        private string manualAppId = string.Empty;

        public event Action? OnRequestClose;

        public RemoteViewModel(string tvIp, string tvLabel)
        {
            _tvIp = tvIp;
            TvLabel = tvLabel;
            Sequences = SamsungRemoteSequences.Sendable.Select(s => new ToolboxSequence(s)).ToList();
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

        // ---- TV toolbox (#635) ----

        /// <summary>
        /// Walks one documented combination through the channel, reporting each press as it goes. What
        /// comes back is delivery, not effect: nothing on this channel says whether the TV acted on the
        /// combo, so the status line says what was sent and leaves the verdict to the screen.
        /// </summary>
        [RelayCommand]
        private async Task SendSequence(ToolboxSequence? row)
        {
            var remote = _remote;
            if (row is null)
                return;

            if (remote is null)
            {
                ToolboxStatus = "lblRemoteNotConnected".Localized();
                return;
            }

            // One toolbox action at a time: a second sequence sent on top of a running one would
            // interleave its presses with the first one's, which is not a combination the TV was
            // ever shown.
            if (IsToolboxBusy)
                return;

            IsToolboxBusy = true;
            try
            {
                var total = row.Sequence.Keys.Count;
                var progress = new Progress<SamsungRemoteKeyDelivery>(d =>
                    ToolboxStatus = string.Format("lblToolboxSending".Localized(), row.Name, d.Index + 1, total));

                var result = await SamsungRemoteSequences.SendAsync(
                    remote, row.Sequence, (int)Math.Round(GapMs), progress);

                ToolboxStatus = result.Completed
                    ? string.Format("lblToolboxSeqDelivered".Localized(), row.Name, total)
                    : string.Format("lblToolboxSeqStopped".Localized(), row.Name, result.DeliveredCount + 1, total);

                IsConnected = remote.IsConnected;
            }
            finally
            {
                IsToolboxBusy = false;
            }
        }

        /// <summary>
        /// Fills the launcher list. The TV is asked what it has installed; whether it answers or not,
        /// the community catalogue is merged in, which is what makes an app the launcher hides
        /// reachable at all.
        /// </summary>
        [RelayCommand]
        private async Task LoadApps()
        {
            var remote = _remote;
            if (remote is null)
            {
                ToolboxStatus = "lblRemoteNotConnected".Localized();
                return;
            }

            if (IsToolboxBusy)
                return;

            IsToolboxBusy = true;
            try
            {
                ToolboxStatus = "lblToolboxAppsLoading".Localized();
                var targets = await SamsungRemoteApps.BuildLauncherListAsync(remote);

                Apps.Clear();
                foreach (var target in targets)
                    Apps.Add(new ToolboxApp(target));

                // Icons come from Samsung's CDN one by one; the list is usable while they arrive.
                _ = LoadIconsAsync(Apps.ToList());

                var reported = targets.Count(t => t.ReportedByTv);
                ToolboxStatus = string.Format("lblToolboxAppsCount".Localized(), targets.Count, reported);
                if (Apps2Samsung.Catalog.SamsungTvAppCatalog.IsOffline)
                    ToolboxStatus += " " + "lblToolboxAppsOffline".Localized();
            }
            finally
            {
                IsToolboxBusy = false;
            }
        }

        [RelayCommand]
        private Task LaunchApp(ToolboxApp? app) =>
            app is null ? Task.CompletedTask : LaunchAsync(app.Target);

        /// <summary>Launches an id typed by hand — an app in neither list, from a forum post or a manual.</summary>
        [RelayCommand]
        private Task LaunchManual()
        {
            var id = ManualAppId?.Trim();
            if (string.IsNullOrEmpty(id))
                return Task.CompletedTask;

            // No name to fall back on, so the DIAL attempt is out; the other two paths take an id.
            return LaunchAsync(new SamsungRemoteLaunchTarget(id, id, IconUrl: null, AppType: 0, ReportedByTv: false));
        }

        private static async Task LoadIconsAsync(IReadOnlyList<ToolboxApp> apps)
        {
            foreach (var app in apps)
                await app.LoadIconAsync();
        }

        private async Task LaunchAsync(SamsungRemoteLaunchTarget target)
        {
            var remote = _remote;
            if (remote is null)
            {
                ToolboxStatus = "lblRemoteNotConnected".Localized();
                return;
            }

            if (IsToolboxBusy)
                return;

            IsToolboxBusy = true;
            try
            {
                ToolboxStatus = string.Format("lblToolboxLaunching".Localized(), target.Name);
                var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target);

                ToolboxStatus = result switch
                {
                    { Succeeded: true, Verified: true } => string.Format("lblToolboxLaunched".Localized(), target.Name),
                    // The message went out and the set never says what became of it — common firmware
                    // behaviour, and not something to dress up as a confirmed launch.
                    { Succeeded: true } => string.Format("lblToolboxLaunchSent".Localized(), target.Name),
                    _ => string.Format("lblToolboxLaunchFailed".Localized(), target.Name),
                };

                IsConnected = remote.IsConnected;
            }
            finally
            {
                IsToolboxBusy = false;
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

    /// <summary>One row of the toolbox's sequence list, with the Core sequence's keys spelled out.</summary>
    public sealed class ToolboxSequence
    {
        public ToolboxSequence(SamsungRemoteSequence sequence)
        {
            Sequence = sequence;
            Name = sequence.NameKey.Localized();
            Description = sequence.DescriptionKey.Localized();
            Caveat = sequence.CaveatKey?.Localized() ?? string.Empty;
            // "KEY_MUTE" reads as "MUTE" — the label on the physical remote, which is what the manuals
            // that document these combinations print.
            Keys = string.Join(" · ", sequence.Keys.Select(k => k.StartsWith("KEY_", StringComparison.Ordinal) ? k[4..] : k));
        }

        public SamsungRemoteSequence Sequence { get; }
        public string Name { get; }
        public string Description { get; }
        public string Caveat { get; }
        public bool HasCaveat => !string.IsNullOrEmpty(Caveat);
        public string Keys { get; }
    }

    /// <summary>One row of the toolbox's launcher list.</summary>
    public sealed partial class ToolboxApp : ObservableObject
    {
        // The icons are store URLs on Samsung's CDN, shared between rows and between openings of the
        // window, so fetch each one once per run.
        private static readonly System.Net.Http.HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> IconCache = new();

        public ToolboxApp(SamsungRemoteLaunchTarget target)
        {
            Target = target;
            Origin = target.ReportedByTv ? string.Empty : "lblToolboxNotOnTv".Localized();
        }

        public SamsungRemoteLaunchTarget Target { get; }
        public string Name => Target.Name;
        public string AppId => Target.AppId;

        [ObservableProperty]
        private Avalonia.Media.Imaging.Bitmap? iconBitmap;

        /// <summary>Empty for an app the TV listed; otherwise the "not listed by the TV" note.</summary>
        public string Origin { get; }
        public bool HasOrigin => !string.IsNullOrEmpty(Origin);

        /// <summary>Best-effort: a row whose icon can't be fetched simply shows none.</summary>
        public async Task LoadIconAsync()
        {
            var url = Target.IconUrl;
            if (string.IsNullOrEmpty(url))
                return;

            if (IconCache.TryGetValue(url, out var cached))
            {
                IconBitmap = cached;
                return;
            }

            try
            {
                var bytes = await IconHttp.GetByteArrayAsync(url);
                using var stream = new System.IO.MemoryStream(bytes);
                var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                IconCache[url] = bitmap;
                IconBitmap = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[toolbox] could not load the icon for {Name}: {ex.Message}");
                IconCache[url] = null;
            }
        }
    }
}
