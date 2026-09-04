using Apps2Samsung.Catalog;
using Apps2Samsung.Agent;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
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
    /// The TV toolbox (#635): opening an app by id, and sending a documented service-menu key
    /// combination. The key combinations go over the remote channel, which needs no Developer Mode —
    /// the point, since the sets this is for (hospitality firmware, a dead or numberless remote) may
    /// have none to switch on.
    /// <para>
    /// The system-app list is the exception: the TV's own menus were never store apps, so no deep link
    /// addresses them — and, it turned out, SDB's launcher refuses them too (#641, then #34). They go
    /// through the debug agent: a small app of ours on the TV that asks the platform directly. The
    /// agent also yields the one honest app list a hospitality set has, hidden flags and all.
    /// </para>
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

        // The developer channel, where this head has one. Null is a route missing, not a failure: the
        // toolbox is offered for any TV on the network, Developer Mode or not.
        private readonly ISdbEngine? _sdb;

        private SamsungRemoteClient? _remote;

        // How this head puts a .wgt on the TV — certificate, resign, push — so the agent installs
        // like any package. Null where the head can't (no installer wired up); the agent then has to
        // be on the set already.
        private readonly Func<string, Action<string>, Task<bool>>? _installWgt;

        private DebugAgentClient? _agent;

        // The full list behind the filtered one shown.
        private IReadOnlyList<SamsungRemoteLaunchTarget> _targets = Array.Empty<SamsungRemoteLaunchTarget>();

        // Everything the agent reported installed, behind the filtered AgentApps.
        private IReadOnlyList<DebugAgentApp> _agentApps = Array.Empty<DebugAgentApp>();

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

        /// <summary>
        /// The built-in apps a hospitality set hides — the hotel menu above all (#641). Fixed, not
        /// filtered: it is a short list of ids to try in order, not something to search.
        /// </summary>
        public IReadOnlyList<SamsungSystemAppRow> SystemApps { get; } =
            SamsungSystemApps.Rows(key => key.Localized());

        /// <summary>What the agent-app filter currently leaves visible.</summary>
        public ObservableCollection<ToolboxAgentApp> AgentApps { get; } = new();

        /// <summary>Whether this head has a developer channel to reach the agent over at all.</summary>
        public bool HasSdb => _sdb is not null;

        [ObservableProperty]
        private bool isAgentAttached;

        /// <summary>The agent's own status line, separate from the channel's.</summary>
        [ObservableProperty]
        private string agentStatus = string.Empty;

        [ObservableProperty]
        private string agentFilter = string.Empty;

        /// <summary>Show only the apps the platform flags as hidden — the set the launcher won't.</summary>
        [ObservableProperty]
        private bool agentHiddenOnly;

        /// <summary>An expression to run inside the agent, for what isn't a button yet.</summary>
        [ObservableProperty]
        private string agentExpression = string.Empty;

        [ObservableProperty]
        private string agentExpressionResult = string.Empty;

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

        public TvToolboxViewModel(
            string tvIp, string tvLabel, ISdbEngine? sdb = null, Func<string, Action<string>, Task<bool>>? installWgt = null)
        {
            _tvIp = tvIp;
            TvLabel = tvLabel;
            _sdb = sdb;
            _installWgt = installWgt;
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

        // Through the agent where it is attached: that is the route that reaches these. Without it
        // the SDB attempt still runs, so the user sees the TV's own refusal rather than a toast.
        [RelayCommand]
        private Task LaunchSystemApp(SamsungSystemAppRow? row) =>
            row is null ? Task.CompletedTask
            : _agent is not null ? LaunchViaAgentAsync(row.AppId, row.Name, control: false)
            : LaunchAsync(row.Target);

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

            // A system app is launched over SDB, which is a different channel entirely — so an
            // unpaired set only blocks the launches that actually need the remote one.
            if (remote is null && _sdb is null)
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
                var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target, _sdb);

                StatusText = result switch
                {
                    { Succeeded: true, Verified: true } => string.Format("lblToolboxLaunched".Localized(), target.Name),
                    // The message went out and the set never says what became of it — common firmware
                    // behaviour, and not something to dress up as a confirmed launch.
                    { Succeeded: true } => string.Format("lblToolboxLaunchSent".Localized(), target.Name),
                    // The launcher's own verdicts, in its words: no route below it can do better.
                    { NotASmartHubApp: true } => string.Format("lblToolboxLaunchNotSmartHub".Localized(), target.Name, result.TvReply),
                    { TvReply: not null } => string.Format("lblToolboxLaunchRefused".Localized(), target.Name, result.TvReply),
                    _ => string.Format("lblToolboxLaunchFailed".Localized(), target.Name),
                };

                IsConnected = remote?.IsConnected == true;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ---------------------------------------------------------------------------------------
        // The debug agent (#34)
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Installs the agent if the TV doesn't list it, starts it in debug mode, attaches, and reads
        /// the platform and the app list. Every step reports into <see cref="AgentStatus"/>.
        /// </summary>
        [RelayCommand]
        private async Task AttachAgent()
        {
            if (_sdb is null)
            {
                AgentStatus = "lblToolboxAgentNeedsSdb".Localized();
                return;
            }

            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                await DetachAgentCoreAsync();

                if (!await DebugAgentClient.IsInstalledAsync(_sdb, _tvIp))
                {
                    if (_installWgt is null)
                    {
                        AgentStatus = "lblToolboxAgentNotInstalled".Localized();
                        return;
                    }

                    AgentStatus = "lblToolboxAgentInstalling".Localized();
                    var wgt = await DebugAgentPackage.WriteAsync(DebugAgentPackage.DefaultDirectory);
                    if (!await _installWgt(wgt, message => AgentStatus = message))
                    {
                        AgentStatus = "lblToolboxAgentInstallFailed".Localized();
                        return;
                    }
                }

                var progress = new Progress<string>(key => AgentStatus = key.Localized());
                var agent = await DebugAgentClient.AttachAsync(_sdb, _tvIp, progress);
                agent.Disconnected += OnAgentDisconnected;
                _agent = agent;

                var platform = await agent.PlatformAsync();
                _agentApps = await agent.ListAppsAsync();
                ApplyAgentFilter();

                IsAgentAttached = true;
                AgentStatus = string.Format("lblToolboxAgentAttached".Localized(),
                    agent.AgentVersion, _agentApps.Count, _agentApps.Count(a => !a.Show), platform.Tizen ?? "?");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[toolbox] agent attach failed: {ex}");
                AgentStatus = string.Format("lblToolboxAgentFailed".Localized(), ex.Message);
                await DetachAgentCoreAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task DetachAgent()
        {
            await DetachAgentCoreAsync();
            AgentStatus = "lblToolboxAgentDetached".Localized();
        }

        private async Task DetachAgentCoreAsync()
        {
            var agent = _agent;
            _agent = null;
            IsAgentAttached = false;
            _agentApps = Array.Empty<DebugAgentApp>();
            AgentApps.Clear();

            if (agent is not null)
            {
                agent.Disconnected -= OnAgentDisconnected;
                await agent.DisposeAsync();
            }
        }

        // Raised off the UI thread by the inspector's receive loop.
        private void OnAgentDisconnected(string? reason) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (_agent is null)
                    return;
                await DetachAgentCoreAsync();
                AgentStatus = string.Format("lblToolboxAgentDisconnected".Localized(), reason ?? string.Empty);
            });

        partial void OnAgentFilterChanged(string value) => ApplyAgentFilter();
        partial void OnAgentHiddenOnlyChanged(bool value) => ApplyAgentFilter();

        private void ApplyAgentFilter()
        {
            var filter = AgentFilter?.Trim() ?? string.Empty;
            var matches = _agentApps.Where(a =>
                (!AgentHiddenOnly || !a.Show) &&
                (string.IsNullOrEmpty(filter) ||
                 a.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                 a.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                 a.PackageId.Contains(filter, StringComparison.OrdinalIgnoreCase)));

            AgentApps.Clear();
            foreach (var app in matches)
                AgentApps.Add(new ToolboxAgentApp(app, "lblToolboxAgentHidden".Localized()));
        }

        [RelayCommand]
        private Task LaunchAgentApp(ToolboxAgentApp? app) =>
            app is null ? Task.CompletedTask : LaunchViaAgentAsync(app.AppId, app.Name, control: false);

        [RelayCommand]
        private Task LaunchAgentAppControl(ToolboxAgentApp? app) =>
            app is null ? Task.CompletedTask : LaunchViaAgentAsync(app.AppId, app.Name, control: true);

        /// <summary>
        /// One launch through the agent, reported in the platform's own terms: accepted and running,
        /// accepted with no context, refused with the error's name, or the agent gone quiet because
        /// something took the screen.
        /// </summary>
        private async Task LaunchViaAgentAsync(string appId, string name, bool control)
        {
            var agent = _agent;
            if (agent is null)
            {
                StatusText = "lblToolboxAgentNotAttached".Localized();
                return;
            }

            if (IsBusy)
                return;

            IsBusy = true;
            try
            {
                // App control goes to an operation the target actually registers, when the probe finds
                // one; `main` is only the guess for a target that registers none of the probed set.
                string? operation = null;
                if (control)
                {
                    StatusText = string.Format("lblToolboxAgentProbing".Localized(), name);
                    operation = (await agent.ProbeAppControlsAsync(appId)).FirstOrDefault();
                }

                StatusText = string.Format("lblToolboxAgentLaunching".Localized(), name);
                var result = control
                    ? await agent.LaunchControlAsync(appId, operation ?? DebugAgentClient.MainOperation)
                    : await agent.LaunchAsync(appId);

                var status = result.State switch
                {
                    DebugAgentLaunchState.Launched => string.Format("lblToolboxAgentLaunched".Localized(), name),
                    DebugAgentLaunchState.LaunchedNoContext => string.Format("lblToolboxAgentLaunchedNoContext".Localized(), name),
                    DebugAgentLaunchState.Refused => string.Format("lblToolboxAgentLaunchRefused".Localized(), name, result.ErrorName, result.ErrorMessage),
                    _ => string.Format("lblToolboxAgentUnresponsive".Localized(), name),
                };
                if (control)
                    status += " " + string.Format("lblToolboxAgentOperationUsed".Localized(), operation ?? DebugAgentClient.MainOperation);

                // A refusal or a launch that went nowhere is the moment to learn what the target does
                // answer to: the operations it registers decide the next attempt.
                if (!control && result.State is DebugAgentLaunchState.Refused or DebugAgentLaunchState.LaunchedNoContext)
                {
                    StatusText = status;
                    var operations = await agent.ProbeAppControlsAsync(appId);
                    status += " " + (operations.Count > 0
                        ? string.Format("lblToolboxAgentOperations".Localized(), name, string.Join(", ", operations.Select(ShortOperation)))
                        : string.Format("lblToolboxAgentOperationsNone".Localized(), name, DebugAgentClient.ProbedOperationCount));
                }

                StatusText = status;
            }
            catch (Exception ex)
            {
                StatusText = string.Format("lblToolboxAgentFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // "http://tizen.org/appcontrol/operation/main" reads as "tizen.org/main" in a status line.
        private static string ShortOperation(string operation) =>
            operation.Replace("http://", string.Empty).Replace("/appcontrol/operation/", "/");

        /// <summary>Runs <see cref="AgentExpression"/> inside the agent and shows what came back, verbatim.</summary>
        [RelayCommand]
        private async Task EvaluateAgent()
        {
            var agent = _agent;
            var expression = AgentExpression?.Trim();
            if (agent is null)
            {
                AgentExpressionResult = "lblToolboxAgentNotAttached".Localized();
                return;
            }
            if (string.IsNullOrEmpty(expression))
                return;

            try
            {
                AgentExpressionResult = await agent.EvaluateAsync(expression);
            }
            catch (Exception ex)
            {
                AgentExpressionResult = ex.Message;
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
            await DetachAgentCoreAsync();

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

    /// <summary>One row of the agent's app list: what the platform reports installed.</summary>
    public sealed class ToolboxAgentApp
    {
        public ToolboxAgentApp(DebugAgentApp app, string hiddenLabel)
        {
            App = app;
            HiddenLabel = app.Show ? string.Empty : hiddenLabel;
        }

        public DebugAgentApp App { get; }
        public string Name => App.DisplayName;
        public string AppId => App.Id;
        public string Version => App.Version;
        public bool IsHidden => !App.Show;

        /// <summary>"hidden" for an app the platform's launcher does not show; empty for the rest.</summary>
        public string HiddenLabel { get; }
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
