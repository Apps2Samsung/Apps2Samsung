using Apps2Samsung.Catalog;
using Apps2Samsung.Agent;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The TV toolbox (#635): opening an app by ID, and sending a documented service-menu key combination.
/// The key combinations ride the <c>samsung.remote.control</c> channel, which needs no Developer Mode —
/// the point, since the sets this exists for (hospitality firmware, a dead or numberless remote) may
/// have none to switch on. The phone is the natural host: it is what you are holding when the remote
/// has died.
/// <para>
/// The system-app list is the exception: the TV's own menus were never store apps, so no deep link
/// addresses them — and, it turned out, SDB's launcher refuses them too (#641, then #34). They go
/// through the debug agent: a small app of ours on the TV that asks the platform directly. The agent
/// also yields the one honest app list a hospitality set has, hidden flags and all.
/// </para>
/// <para>
/// Its own page rather than a panel under the remote's keys: these are not remote buttons. And the app
/// list here is not the Installed apps page — that one goes over SDB, needs Developer Mode, and reports
/// what is really installed; this one launches by ID whether or not the TV admits to having the app.
/// </para>
/// </summary>
public partial class TvToolboxPage : ContentPage
{
	private readonly string _tvIp;
	private readonly string _tvLabel;

	// The developer channel, where the phone has one. Null is a route missing, not a failure: the
	// toolbox is offered for any TV on the network, Developer Mode or not.
	private readonly ISdbEngine? _sdb;
	private SamsungRemoteClient? _remote;

	// How this head puts a .wgt on the TV — certificate, resign, push — so the agent installs like
	// any package. Null where the caller has no installer; the agent then has to be on the set already.
	private readonly Func<string, Action<string>, Task<bool>>? _installWgt;
	private DebugAgentClient? _agent;

	// The full list behind the filtered one shown.
	private IReadOnlyList<SamsungRemoteLaunchTarget> _targets = Array.Empty<SamsungRemoteLaunchTarget>();

	// Everything the agent reported installed, behind the filtered list shown.
	private IReadOnlyList<DebugAgentApp> _agentApps = Array.Empty<DebugAgentApp>();

	// One action at a time: a second combination sent on top of a running one would interleave its
	// presses with the first one's, which is not a combination the TV was ever shown.
	private bool _busy;

	public TvToolboxPage(ISdbEngine? sdb, string tvIp, string tvLabel, Func<string, Action<string>, Task<bool>>? installWgt = null)
	{
		InitializeComponent();
		_tvIp = tvIp;
		_tvLabel = tvLabel;
		_sdb = sdb;
		_installWgt = installWgt;
		AgentCard.IsEnabled = sdb is not null;
		AgentNeedsSdbHint.IsVisible = sdb is null;

		// The TV's own menus, addressed by id (#641). Fixed, not filtered: a short list to try in
		// order, not something to search.
		BindableLayout.SetItemsSource(SystemAppList, SamsungSystemApps.Rows(L10n.Get));

		// Buttons only for the combinations this channel can deliver.
		BindableLayout.SetItemsSource(
			SequenceList,
			SamsungRemoteSequences.Sendable.Select(s => new ToolboxSequenceRow(s)).ToList());

		// The standby ones get printed instead: a sleeping set serves no channel, so a button for one
		// could never fire, and they are the sequences most likely to rescue a locked set (#639).
		BindableLayout.SetItemsSource(
			StandbySequenceList,
			SamsungRemoteSequences.StandbyOnly.Select(s => new ToolboxSequenceRow(s)).ToList());

		BindableLayout.SetItemsSource(
			StandbyStepList,
			SamsungRemoteSequences.StandbyStepKeys.Select(L10n.Get).ToList());
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	protected override async void OnDisappearing()
	{
		base.OnDisappearing();
		await DetachAgentAsync();
		var remote = _remote;
		_remote = null;
		if (remote is not null)
			await remote.DisposeAsync();
	}

	/// <summary>
	/// Fills the app list and opens the channel. The list comes first and doesn't wait for the TV: it
	/// is the community catalogue, which is what makes an app the launcher hides reachable, and it
	/// renders even while the set is still being woken.
	/// </summary>
	private async Task LoadAsync()
	{
		_targets = await SamsungRemoteApps.CatalogueTargetsAsync();
		OfflineHint.IsVisible = SamsungTvAppCatalog.IsOffline;
		ApplyFilter();

		await ConnectAsync();
	}

	private async Task ConnectAsync()
	{
		var progress = new Progress<string>(key => SetStatus($"{_tvLabel} — {L10n.Get(key)}"));
		var session = await RemoteSession.ConnectAsync(_tvIp, RemoteCredentials.Instance, progress);

		// Read off the probe, which happens even on the runs that then fail to open the channel — a
		// refused pairing still told us what kind of set this is.
		HospitalityNotice.IsVisible = session.Capability.Supported && session.Capability.IsHospitality;

		if (!session.Connected)
		{
			SetStatus($"{_tvLabel} — {L10n.Get(RemoteCredentials.StatusKeyFor(session.Outcome))}");
			return;
		}

		_remote = session.Client;
		var name = string.IsNullOrWhiteSpace(session.TvName) ? _tvLabel : session.TvName;
		SetStatus($"{name} — {L10n.Get("lblRemoteConnected")}");

		// Older sets answer "what have you got installed?"; Tizen dropped the query around 2020 and
		// most current firmware never replies, so this enriches the list rather than filling it — and
		// it runs after the list is already on screen.
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

	private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

	private void ApplyFilter()
	{
		var filter = AppSearch.Text?.Trim() ?? string.Empty;
		var matches = string.IsNullOrEmpty(filter)
			? _targets
			: _targets.Where(t =>
				t.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
				t.AppId.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

		BindableLayout.SetItemsSource(AppList, matches.Select(t => new ToolboxAppRow(t)).ToList());
	}

	// Through the agent where it is attached: that is the route that reaches these. Without it the
	// SDB attempt still runs, so the user reads the TV's own refusal rather than a toast.
	private async void OnLaunchSystemAppClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: SamsungSystemAppRow row })
			return;

		if (_agent is not null)
			await LaunchViaAgentAsync(row.AppId, row.Name, control: false);
		else
			await LaunchAsync(row.Target);
	}

	private async void OnLaunchAppClicked(object? sender, EventArgs e)
	{
		if (sender is Button button && button.CommandParameter is ToolboxAppRow row)
			await LaunchAsync(row.Target);
	}

	/// <summary>Launches an ID typed by hand — an app in neither list, from a forum post or a manual.</summary>
	private async void OnLaunchManualClicked(object? sender, EventArgs e)
	{
		var id = ManualAppIdEntry.Text?.Trim();
		if (string.IsNullOrEmpty(id))
			return;

		// No name to go with it, so the DIAL attempt is out; the other two paths take an ID.
		await LaunchAsync(new SamsungRemoteLaunchTarget(id, id, IconUrl: null, AppType: 0, ReportedByTv: false));
	}

	private async Task LaunchAsync(SamsungRemoteLaunchTarget target)
	{
		var remote = _remote;

		// A system app is launched over SDB, which is a different channel entirely — so an unpaired
		// set only blocks the launches that actually need the remote one.
		if (remote is null && _sdb is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			SetStatus(string.Format(L10n.Get("lblToolboxLaunching"), target.Name));
			var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target, _sdb);

			SetStatus(result switch
			{
				{ Succeeded: true, Verified: true } => string.Format(L10n.Get("lblToolboxLaunched"), target.Name),
				// The message went out and the set never says what became of it — common firmware
				// behaviour, and not something to dress up as a confirmed launch.
				{ Succeeded: true } => string.Format(L10n.Get("lblToolboxLaunchSent"), target.Name),
				// The launcher's own verdicts, in its words: no route below it can do better.
				{ NotASmartHubApp: true } => string.Format(L10n.Get("lblToolboxLaunchNotSmartHub"), target.Name, result.TvReply),
				{ TvReply: not null } => string.Format(L10n.Get("lblToolboxLaunchRefused"), target.Name, result.TvReply),
				_ => string.Format(L10n.Get("lblToolboxLaunchFailed"), target.Name),
			});
		}
		finally
		{
			_busy = false;
		}
	}

	// ---------------------------------------------------------------------------------------------
	// The debug agent (#34)
	// ---------------------------------------------------------------------------------------------

	/// <summary>
	/// Installs the agent if the TV doesn't list it, starts it in debug mode, attaches, and reads the
	/// platform and the app list. Every step reports into the agent's own status line.
	/// </summary>
	private async void OnAttachAgentClicked(object? sender, EventArgs e)
	{
		if (_sdb is null)
		{
			SetAgentStatus(L10n.Get("lblToolboxAgentNeedsSdb"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		AttachAgentBtn.IsEnabled = false;
		try
		{
			await DetachAgentAsync();

			if (!await DebugAgentClient.IsInstalledAsync(_sdb, _tvIp))
			{
				if (_installWgt is null)
				{
					SetAgentStatus(L10n.Get("lblToolboxAgentNotInstalled"));
					return;
				}

				SetAgentStatus(L10n.Get("lblToolboxAgentInstalling"));
				var wgt = await DebugAgentPackage.WriteAsync(DebugAgentPackage.DefaultDirectory);
				if (!await _installWgt(wgt, message => MainThread.BeginInvokeOnMainThread(() => SetAgentStatus(message))))
				{
					SetAgentStatus(L10n.Get("lblToolboxAgentInstallFailed"));
					return;
				}
			}

			var progress = new Progress<string>(key => SetAgentStatus(L10n.Get(key)));
			var agent = await DebugAgentClient.AttachAsync(_sdb, _tvIp, progress);
			agent.Disconnected += OnAgentDisconnected;
			_agent = agent;

			var platform = await agent.PlatformAsync();
			_agentApps = await agent.ListAppsAsync();
			ApplyAgentFilter();

			AgentSection.IsVisible = true;
			DetachAgentBtn.IsEnabled = true;
			SetAgentStatus(string.Format(L10n.Get("lblToolboxAgentAttached"),
				agent.AgentVersion, _agentApps.Count, _agentApps.Count(a => !a.Show), platform.Tizen ?? "?"));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] agent attach failed: {ex}");
			SetAgentStatus(string.Format(L10n.Get("lblToolboxAgentFailed"), ex.Message));
			await DetachAgentAsync();
		}
		finally
		{
			_busy = false;
			AttachAgentBtn.IsEnabled = true;
		}
	}

	private async void OnDetachAgentClicked(object? sender, EventArgs e)
	{
		await DetachAgentAsync();
		SetAgentStatus(L10n.Get("lblToolboxAgentDetached"));
	}

	private async Task DetachAgentAsync()
	{
		var agent = _agent;
		_agent = null;
		_agentApps = Array.Empty<DebugAgentApp>();
		BindableLayout.SetItemsSource(AgentAppList, Array.Empty<ToolboxAgentAppRow>());
		AgentSection.IsVisible = false;
		DetachAgentBtn.IsEnabled = false;

		if (agent is not null)
		{
			agent.Disconnected -= OnAgentDisconnected;
			await agent.DisposeAsync();
		}
	}

	// Raised off the UI thread by the inspector's receive loop.
	private void OnAgentDisconnected(string? reason) => MainThread.BeginInvokeOnMainThread(async () =>
	{
		if (_agent is null)
			return;
		await DetachAgentAsync();
		SetAgentStatus(string.Format(L10n.Get("lblToolboxAgentDisconnected"), reason ?? string.Empty));
	});

	private void OnAgentFilterChanged(object? sender, EventArgs e) => ApplyAgentFilter();

	private void ApplyAgentFilter()
	{
		var filter = AgentSearch.Text?.Trim() ?? string.Empty;
		var hiddenOnly = AgentHiddenOnly.IsToggled;
		var hiddenLabel = L10n.Get("lblToolboxAgentHidden");

		var rows = _agentApps
			.Where(a => !hiddenOnly || !a.Show)
			.Where(a => string.IsNullOrEmpty(filter) ||
						a.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
						a.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
						a.PackageId.Contains(filter, StringComparison.OrdinalIgnoreCase))
			.Select(a => new ToolboxAgentAppRow(a, hiddenLabel))
			.ToList();

		BindableLayout.SetItemsSource(AgentAppList, rows);
	}

	private async void OnLaunchAgentAppClicked(object? sender, EventArgs e)
	{
		if (sender is Button { CommandParameter: ToolboxAgentAppRow row })
			await LaunchViaAgentAsync(row.AppId, row.Name, control: false);
	}

	private async void OnLaunchAgentAppControlClicked(object? sender, EventArgs e)
	{
		if (sender is Button { CommandParameter: ToolboxAgentAppRow row })
			await LaunchViaAgentAsync(row.AppId, row.Name, control: true);
	}

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
			SetStatus(L10n.Get("lblToolboxAgentNotAttached"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			// App control goes to an operation the target actually registers, when the probe finds one;
			// `main` is only the guess for a target that registers none of the probed set.
			string? operation = null;
			if (control)
			{
				SetStatus(string.Format(L10n.Get("lblToolboxAgentProbing"), name));
				operation = (await agent.ProbeAppControlsAsync(appId)).FirstOrDefault();
			}

			SetStatus(string.Format(L10n.Get("lblToolboxAgentLaunching"), name));
			var result = control
				? await agent.LaunchControlAsync(appId, operation ?? DebugAgentClient.MainOperation)
				: await agent.LaunchAsync(appId);

			var status = result.State switch
			{
				DebugAgentLaunchState.Launched => string.Format(L10n.Get("lblToolboxAgentLaunched"), name),
				DebugAgentLaunchState.LaunchedNoContext => string.Format(L10n.Get("lblToolboxAgentLaunchedNoContext"), name),
				DebugAgentLaunchState.Refused => string.Format(L10n.Get("lblToolboxAgentLaunchRefused"), name, result.ErrorName, result.ErrorMessage),
				_ => string.Format(L10n.Get("lblToolboxAgentUnresponsive"), name),
			};
			if (control)
				status += " " + string.Format(L10n.Get("lblToolboxAgentOperationUsed"), operation ?? DebugAgentClient.MainOperation);

			// A refusal or a launch that went nowhere is the moment to learn what the target does
			// answer to: the operations it registers decide the next attempt.
			if (!control && result.State is DebugAgentLaunchState.Refused or DebugAgentLaunchState.LaunchedNoContext)
			{
				SetStatus(status);
				var operations = await agent.ProbeAppControlsAsync(appId);
				status += " " + (operations.Count > 0
					? string.Format(L10n.Get("lblToolboxAgentOperations"), name, string.Join(", ", operations.Select(ShortOperation)))
					: string.Format(L10n.Get("lblToolboxAgentOperationsNone"), name, DebugAgentClient.ProbedOperationCount));
			}

			SetStatus(status);
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("lblToolboxAgentFailed"), ex.Message));
		}
		finally
		{
			_busy = false;
		}
	}

	// "http://tizen.org/appcontrol/operation/main" reads as "tizen.org/main" in a status line.
	private static string ShortOperation(string operation) =>
		operation.Replace("http://", string.Empty).Replace("/appcontrol/operation/", "/");

	/// <summary>Runs the typed expression inside the agent and shows what came back, verbatim.</summary>
	private async void OnEvaluateAgentClicked(object? sender, EventArgs e)
	{
		var agent = _agent;
		var expression = AgentExpression.Text?.Trim();
		if (agent is null)
		{
			AgentExpressionResult.Text = L10n.Get("lblToolboxAgentNotAttached");
			return;
		}
		if (string.IsNullOrEmpty(expression))
			return;

		try
		{
			AgentExpressionResult.Text = await agent.EvaluateAsync(expression);
		}
		catch (Exception ex)
		{
			AgentExpressionResult.Text = ex.Message;
		}
	}

	private void SetAgentStatus(string message) => AgentStatusLabel.Text = message;

	/// <summary>
	/// Walks one documented combination through the channel, reporting each press as it goes. What
	/// comes back is delivery, not effect: nothing on this channel says whether the TV acted on the
	/// combination, so the status line says what was sent and leaves the verdict to the screen.
	/// </summary>
	private async void OnSendSequenceClicked(object? sender, EventArgs e)
	{
		if (sender is not Button button || button.CommandParameter is not ToolboxSequenceRow row)
			return;

		var remote = _remote;
		if (remote is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			var total = row.Sequence.Keys.Count;
			var progress = new Progress<SamsungRemoteKeyDelivery>(d =>
				SetStatus(string.Format(L10n.Get("lblToolboxSending"), row.Name, d.Index + 1, total)));

			var result = await SamsungRemoteSequences.SendAsync(
				remote, row.Sequence, (int)Math.Round(GapSlider.Value), progress);

			SetStatus(result.Completed
				? string.Format(L10n.Get("lblToolboxSeqDelivered"), row.Name, total)
				: string.Format(L10n.Get("lblToolboxSeqStopped"), row.Name, result.DeliveredCount + 1, total));
		}
		finally
		{
			_busy = false;
		}
	}

	private async void OnReloadClicked(object? sender, EventArgs e)
	{
		var remote = _remote;
		_remote = null;
		if (remote is not null)
			await remote.DisposeAsync();

		await LoadAsync();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void SetStatus(string message) => StatusLabel.Text = message;
}

/// <summary>One row of the sequence list, with the Core sequence's keys spelled out.</summary>
public sealed class ToolboxSequenceRow
{
	public ToolboxSequenceRow(SamsungRemoteSequence sequence)
	{
		Sequence = sequence;
		Name = L10n.Get(sequence.NameKey);
		Description = L10n.Get(sequence.DescriptionKey);
		Caveat = sequence.CaveatKey is null ? string.Empty : L10n.Get(sequence.CaveatKey);
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
public sealed class ToolboxAgentAppRow
{
	public ToolboxAgentAppRow(DebugAgentApp app, string hiddenLabel)
	{
		App = app;
		// The ID is what the launch is addressed to, so it stays visible; the platform's own hidden
		// flag is the one piece of information the launcher never gave us.
		Subtitle = app.Show
			? $"{app.Id} · {app.Version}"
			: $"{app.Id} · {app.Version} · {hiddenLabel}";
	}

	public DebugAgentApp App { get; }
	public string Name => App.DisplayName;
	public string AppId => App.Id;
	public string Subtitle { get; }
	public bool IsHidden => !App.Show;
}

/// <summary>One row of the app list.</summary>
public sealed class ToolboxAppRow
{
	public ToolboxAppRow(SamsungRemoteLaunchTarget target)
	{
		Target = target;
		// The ID is what the launch is addressed to, so it stays visible; apps the set listed itself
		// say so, since those are the ones certain to be there.
		Subtitle = target.ReportedByTv
			? $"{target.AppId} · {L10n.Get("lblToolboxOnTv")}"
			: target.AppId;
	}

	public SamsungRemoteLaunchTarget Target { get; }
	public string Name => Target.Name;
	public string AppId => Target.AppId;
	public string? IconUrl => Target.IconUrl;
	public string Subtitle { get; }
}
