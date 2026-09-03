using Apps2Samsung.Catalog;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// The TV toolbox (#635): opening an app by id, and sending a documented service-menu key
    /// combination. Both go over the remote channel, so neither needs Developer Mode — which is the
    /// point, since the sets this is for (hospitality firmware, a dead or numberless remote) have no
    /// Developer Mode to switch on.
    /// <para>
    /// Its own screen rather than a panel under the remote's keys: these aren't remote buttons, and the
    /// app list is not the installed-apps list (that one goes over SDB, needs Developer Mode, and
    /// reports what is really installed — this one launches by id whether the TV admits to the app or
    /// not).
    /// </para>
    /// </summary>
    public partial class TvToolboxViewModel : ViewModelBase, IAsyncDisposable
    {
        private readonly string _tvIp;
        private SamsungRemoteClient? _remote;

        // The full list behind the filtered one shown.
        private IReadOnlyList<SamsungRemoteLaunchTarget> _targets = Array.Empty<SamsungRemoteLaunchTarget>();

        public string TvLabel { get; }

        /// <summary>The combinations this channel can deliver, as buttons.</summary>
        public IReadOnlyList<ToolboxSequence> Sequences { get; }

        /// <summary>
        /// The combinations that start from standby. No button can reach them — a sleeping set serves
        /// nothing — so they are printed with <see cref="StandbySteps"/> for the physical remote (#639).
        /// </summary>
        public IReadOnlyList<ToolboxSequence> StandbySequences { get; }

        /// <summary>How to enter one of <see cref="StandbySequences"/> by hand, in order.</summary>
        public IReadOnlyList<string> StandbySteps { get; }

        /// <summary>What the filter box currently leaves visible.</summary>
        public ObservableCollection<ToolboxApp> Apps { get; } = new();

        [ObservableProperty]
        private string statusText = string.Empty;

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private string appFilter = string.Empty;

        /// <summary>An id typed by hand, for an app in neither list.</summary>
        [ObservableProperty]
        private string manualAppId = string.Empty;

        /// <summary>Gap between the presses of a combination, in milliseconds.</summary>
        [ObservableProperty]
        private double gapMs = SamsungRemoteSequences.DefaultGapMs;

        /// <summary>Set while the app list is still the catalogue alone (offline, or the TV said nothing).</summary>
        [ObservableProperty]
        private bool isCatalogueOffline;

        /// <summary>
        /// The probe says this is a hospitality set. Worth one line of its own: no Smart Hub means no
        /// app store, which is the single most confusing thing about these TVs — the apps aren't
        /// hidden, there is no store to hide them in (#639).
        /// </summary>
        [ObservableProperty]
        private bool isHospitality;

        public event Action? OnRequestClose;

        public TvToolboxViewModel(string tvIp, string tvLabel)
        {
            _tvIp = tvIp;
            TvLabel = tvLabel;
            Sequences = SamsungRemoteSequences.Sendable.Select(s => new ToolboxSequence(s)).ToList();
            StandbySequences = SamsungRemoteSequences.StandbyOnly.Select(s => new ToolboxSequence(s)).ToList();
            StandbySteps = SamsungRemoteSequences.StandbyStepKeys.Select(k => k.Localized()).ToList();
        }

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();

        /// <summary>
        /// Fills the app list and opens the channel. The list comes first and doesn't wait for the TV:
        /// it is the community catalogue, which is what makes an app the launcher hides reachable, and
        /// it renders even while the set is still being woken.
        /// </summary>
        [RelayCommand]
        private async Task Load()
        {
            IsBusy = true;
            try
            {
                await LoadAppsAsync();
                await ConnectAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadAppsAsync()
        {
            _targets = await SamsungRemoteApps.CatalogueTargetsAsync();
            IsCatalogueOffline = SamsungTvAppCatalog.IsOffline;
            ApplyFilter();
        }

        private async Task ConnectAsync()
        {
            var progress = new Progress<string>(key => StatusText = key.Localized());
            var session = await RemoteSession.ConnectAsync(_tvIp, RemoteStore.Credentials.Instance, progress);

            // Read off the probe, which happens even on the runs that then fail to open the channel —
            // a refused pairing still told us what kind of set this is.
            IsHospitality = session.Capability.Supported && session.Capability.IsHospitality;

            if (!session.Connected)
            {
                StatusText = RemoteStore.StatusKeyFor(session.Outcome).Localized();
                return;
            }

            _remote = session.Client;
            IsConnected = true;
            StatusText = "lblRemoteConnected".Localized();

            // Older sets answer "what have you got installed?"; Tizen dropped the query around 2020 and
            // most current firmware simply never replies, so this enriches the list rather than filling
            // it — and it runs after the list is already on screen.
            _ = AskTheTvAsync(session.Client!);
        }

        private async Task AskTheTvAsync(SamsungRemoteClient client)
        {
            var reported = await SamsungRemoteApps.ListInstalledAsync(client);
            if (reported.Count == 0 || !ReferenceEquals(client, _remote))
                return;

            _targets = SamsungRemoteApps.Merge(await SamsungTvAppCatalog.GetAsync(), reported);
            ApplyFilter();
        }

        partial void OnAppFilterChanged(string value) => ApplyFilter();

        private void ApplyFilter()
        {
            var filter = AppFilter?.Trim() ?? string.Empty;
            var matches = string.IsNullOrEmpty(filter)
                ? _targets
                : _targets.Where(t =>
                    t.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                    t.AppId.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            Apps.Clear();
            foreach (var target in matches)
                Apps.Add(new ToolboxApp(target));

            // Icons arrive from Samsung's CDN one at a time; the list is usable meanwhile.
            _ = LoadIconsAsync(Apps.ToList());
        }

        private static async Task LoadIconsAsync(IReadOnlyList<ToolboxApp> apps)
        {
            foreach (var app in apps)
                await app.LoadIconAsync();
        }

        [RelayCommand]
        private Task LaunchApp(ToolboxApp? app) =>
            app is null ? Task.CompletedTask : LaunchAsync(app.Target);

        [RelayCommand]
        private Task LaunchManual()
        {
            var id = ManualAppId?.Trim();
            if (string.IsNullOrEmpty(id))
                return Task.CompletedTask;

            // No name to go with it, so the DIAL attempt is out; the other two paths take an id.
            return LaunchAsync(new SamsungRemoteLaunchTarget(id, id, IconUrl: null, AppType: 0, ReportedByTv: false));
        }

        private async Task LaunchAsync(SamsungRemoteLaunchTarget target)
        {
            var remote = _remote;
            if (remote is null)
            {
                StatusText = "lblRemoteNotConnected".Localized();
                return;
            }

            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                StatusText = string.Format("lblToolboxLaunching".Localized(), target.Name);
                var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target);

                StatusText = result switch
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
                IsBusy = false;
            }
        }

        /// <summary>
        /// Walks one documented combination through the channel, reporting each press as it goes. What
        /// comes back is delivery, not effect: nothing on this channel says whether the TV acted on the
        /// combination, so the status line says what was sent and leaves the verdict to the screen.
        /// </summary>
        [RelayCommand]
        private async Task SendSequence(ToolboxSequence? row)
        {
            if (row is null)
                return;

            var remote = _remote;
            if (remote is null)
            {
                StatusText = "lblRemoteNotConnected".Localized();
                return;
            }

            // One at a time: a second combination sent on top of a running one would interleave its
            // presses with the first one's, which is not a combination the TV was ever shown.
            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                var total = row.Sequence.Keys.Count;
                var progress = new Progress<SamsungRemoteKeyDelivery>(d =>
                    StatusText = string.Format("lblToolboxSending".Localized(), row.Name, d.Index + 1, total));

                var result = await SamsungRemoteSequences.SendAsync(
                    remote, row.Sequence, (int)Math.Round(GapMs), progress);

                StatusText = result.Completed
                    ? string.Format("lblToolboxSeqDelivered".Localized(), row.Name, total)
                    : string.Format("lblToolboxSeqStopped".Localized(), row.Name, result.DeliveredCount + 1, total);

                IsConnected = remote.IsConnected;
            }
            finally
            {
                IsBusy = false;
            }
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

    /// <summary>One row of the sequence list, with the Core sequence's keys spelled out.</summary>
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

    /// <summary>One row of the app list.</summary>
    public sealed partial class ToolboxApp : ObservableObject
    {
        // The icons are store URLs on Samsung's CDN, shared between rows and between openings of the
        // window, so fetch each one once per run.
        private static readonly System.Net.Http.HttpClient IconHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> IconCache = new();

        public ToolboxApp(SamsungRemoteLaunchTarget target)
        {
            Target = target;
            Origin = target.ReportedByTv ? "lblToolboxOnTv".Localized() : string.Empty;
        }

        public SamsungRemoteLaunchTarget Target { get; }
        public string Name => Target.Name;
        public string AppId => Target.AppId;

        [ObservableProperty]
        private Avalonia.Media.Imaging.Bitmap? iconBitmap;

        /// <summary>"On this TV" for an app the set itself listed; empty for the rest.</summary>
        public string Origin { get; }
        public bool HasOrigin => !string.IsNullOrEmpty(Origin);

        /// <summary>Best-effort: a row whose icon can't be fetched simply shows none.</summary>
        public async Task LoadIconAsync()
        {
            var url = Target.IconUrl;
            if (string.IsNullOrEmpty(url) || IconBitmap is not null)
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
