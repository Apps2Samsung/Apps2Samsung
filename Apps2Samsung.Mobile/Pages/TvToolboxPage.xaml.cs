using Apps2Samsung.Catalog;
using Apps2Samsung.Agent;
using Apps2Samsung.Certificate;
using Apps2Samsung.Install;
using Apps2Samsung.Packaging;
using Apps2Samsung.Localization;
using Apps2Samsung.TvStore;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Remote;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The TV toolbox (#635): opening an app by ID, and sending a documented service-menu key combination.
/// The key combinations ride the <c>samsung.remote.control</c> channel, which needs no Developer Mode , 
/// the point, since the sets this exists for (hospitality firmware, a dead or numberless remote) may
/// have none to switch on. The phone is the natural host: it is what you are holding when the remote
/// has died.
/// <para>
/// The system-app list is the exception: the TV's own menus were never store apps, so no deep link
/// addresses them, and, it turned out, SDB's launcher refuses them too (#641, then #34). They go
/// through the debug agent: a small app of ours on the TV that asks the platform directly. The agent
/// also yields the one honest app list a hospitality set has, hidden flags and all.
/// </para>
/// <para>
/// Its own page rather than a panel under the remote's keys: these are not remote buttons. And the app
/// list here is not the Installed apps page, that one goes over SDB, needs Developer Mode, and reports
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
	// From the probe: a consumer set that reports Smart Hub, whose app-status endpoint can be trusted
	// to say "no such app" (see SamsungRemoteApps.LaunchAsync).
	private bool _isConsumerSet;

	// How this head puts a .wgt on the TV, certificate, resign, push, so the agent installs like
	// any package. Null where the caller has no installer; the agent then has to be on the set already.
	private readonly Func<string, Action<string>, Task<bool>>? _installWgt;
	private DebugAgentClient? _agent;

	// The head's certificate flow for this TV (#844): the signing profile an install would use, made
	// (with the Samsung sign-in sheet) when none covers the set yet; null when that was cancelled.
	// What Arm TV-Store hands over. Null where the caller has no certificate flow.
	private readonly Func<bool, Action<string>, Task<SigningProfile?>>? _signingProfile;

	// The Package file section (#979): a .wgt/.tpk from the phone (a cache copy, never the user's
	// file), and the install's customization step for a package that goes to TV-Store.
	private readonly Func<Task<string?>>? _pickPackage;
	private readonly Func<string, string, Action<string>?, Task>? _customize;

	// The full list behind the filtered one shown.
	private IReadOnlyList<SamsungRemoteLaunchTarget> _targets = Array.Empty<SamsungRemoteLaunchTarget>();

	// Everything the agent reported installed, behind the filtered list shown.
	private IReadOnlyList<DebugAgentApp> _agentApps = Array.Empty<DebugAgentApp>();

	// One action at a time: a second combination sent on top of a running one would interleave its
	// presses with the first one's, which is not a combination the TV was ever shown.
	private bool _busy;

	public TvToolboxPage(ISdbEngine? sdb, string tvIp, string tvLabel, Func<string, Action<string>, Task<bool>>? installWgt = null,
		Func<bool, Action<string>, Task<SigningProfile?>>? signingProfile = null,
		Func<Task<string?>>? pickPackage = null, Func<string, string, Action<string>?, Task>? customize = null)
	{
		InitializeComponent();
		_tvIp = tvIp;
		_tvLabel = tvLabel;
		_sdb = sdb;
		_installWgt = installWgt;
		_signingProfile = signingProfile;
		_pickPackage = pickPackage;
		_customize = customize;
		SignPackageBtn.IsEnabled = sdb is not null && pickPackage is not null && signingProfile is not null;
		SendToTvStoreBtn.IsEnabled = pickPackage is not null;
		AgentChip.IsEnabled = sdb is not null;
		StagingAttachBtn.IsEnabled = sdb is not null;
		AgentNeedsSdbHint.IsVisible = sdb is null;
		ArmTvStoreBtn.IsEnabled = sdb is not null;
		TvStoreNeedsSdbHint.IsVisible = sdb is null;
		ShowTvSources();
		ExecuteManualBtn.IsEnabled = sdb is not null;

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

		// One tool open at a time (#1041): opening a card closes the one that was open.
		var cards = new[] { AppsCard, SystemAppsCard, TvStoreCard, PackageCard, StagingCard, SequencesCard, StandbyCard };
		foreach (var card in cards)
			card.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName != nameof(Apps2Samsung.Mobile.Controls.ExpanderSection.IsExpanded) || !card.IsExpanded)
					return;
				foreach (var other in cards)
					if (!ReferenceEquals(other, card))
						other.IsExpanded = false;
			};
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
		var progress = new Progress<string>(key => SetStatus($"{_tvLabel} · {L10n.Get(key)}"));
		var session = await RemoteSession.ConnectAsync(_tvIp, RemoteCredentials.Instance, progress);

		// Read off the probe, which happens even on the runs that then fail to open the channel, a
		// refused pairing still told us what kind of set this is.
		HospitalityNotice.IsVisible = session.Capability.Supported && session.Capability.IsHospitality;
		_isConsumerSet = session.Capability.Supported && session.Capability.IsConsumer;
		ConsumerNotice.IsVisible = _isConsumerSet;

		if (!session.Connected)
		{
			SetStatus($"{_tvLabel} · {L10n.Get(RemoteCredentials.StatusKeyFor(session.Outcome))}");
			return;
		}

		_remote = session.Client;
		var name = string.IsNullOrWhiteSpace(session.TvName) ? _tvLabel : session.TvName;
		SetStatus($"{name} · {L10n.Get("lblRemoteConnected")}");

		// Older sets answer "what have you got installed?"; Tizen dropped the query around 2020 and
		// most current firmware never replies, so this enriches the list rather than filling it, and
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

	/// <summary>Launches an ID typed by hand, an app in neither list, from a forum post or a manual.</summary>
	private async void OnLaunchManualClicked(object? sender, EventArgs e)
	{
		var id = ManualAppIdEntry.Text?.Trim();
		if (string.IsNullOrEmpty(id))
			return;

		// No name to go with it, so the DIAL attempt is out; the other two paths take an ID.
		await LaunchAsync(new SamsungRemoteLaunchTarget(id, id, IconUrl: null, AppType: 0, ReportedByTv: false));
	}

	/// <summary>
	/// <c>0 execute &lt;id&gt;</c> over SDB, the platform launcher rather than Smart Hub's, with the TV's
	/// reply shown as it came. For a service or an ID <c>was_execute</c> answers [400] to.
	/// </summary>
	private async void OnExecuteManualClicked(object? sender, EventArgs e)
	{
		var id = ManualAppIdEntry.Text?.Trim();
		if (string.IsNullOrEmpty(id))
			return;

		if (_sdb is null)
		{
			SetStatus(L10n.Get("lblToolboxExecuteNeedsSdb"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			SetStatus(string.Format(L10n.Get("lblToolboxExecuteSending"), id));
			var result = await _sdb.ExecuteAsync(_tvIp, id);
			var reply = (result.ExitCode == 0 ? result.Output : (string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)).Trim();
			SetStatus(result.ExitCode != 0
				? string.Format(L10n.Get("lblToolboxExecuteFailed"), id, reply)
				: reply.Length == 0
					? string.Format(L10n.Get("lblToolboxExecuteNoAnswer"), id)
					: string.Format(L10n.Get("lblToolboxExecuteReply"), id, reply.Replace("\r", string.Empty).Replace("\n", " | ")));
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("lblToolboxExecuteFailed"), id, ex.Message));
		}
		finally
		{
			_busy = false;
		}
	}

	private async Task LaunchAsync(SamsungRemoteLaunchTarget target)
	{
		var remote = _remote;

		// A system app is launched over SDB, which is a different channel entirely, so an unpaired
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
			var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target, _sdb, trustNotInstalled: _isConsumerSet);

			SetStatus(result switch
			{
				{ Succeeded: true, Verified: true } => string.Format(L10n.Get("lblToolboxLaunched"), target.Name),
				// The message went out and the set never says what became of it, common firmware
				// behaviour, and not something to dress up as a confirmed launch.
				{ Succeeded: true } => string.Format(L10n.Get("lblToolboxLaunchSent"), target.Name),
				// The launcher's own verdicts, in its words: no route below it can do better.
				{ NotInstalled: true } => string.Format(L10n.Get("lblToolboxLaunchNotInstalled"), target.Name),
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
	// Install leftovers, through the agent (sdbd's own "0 rmfile" deletes nothing on retail firmware)
	// ---------------------------------------------------------------------------------------------

	/// <summary>Reads the staging folder again. Read-only; the list and the status line follow.</summary>
	private async void OnRefreshStagingClicked(object? sender, EventArgs e)
	{
		var agent = _agent;
		if (agent is null)
		{
			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingNeedsAgent");
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			await RefreshStagingAsync(agent);
		}
		finally
		{
			_busy = false;
		}
	}

	private async Task RefreshStagingAsync(DebugAgentClient agent)
	{
		if (!agent.SupportsStaging)
		{
			BindableLayout.SetItemsSource(StagedFileList, Array.Empty<ToolboxStagedFileRow>());
			ClearStagingBtn.IsEnabled = false;
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingAgentTooOld"), agent.AgentVersion, DebugAgentClient.StagingSince);
			return;
		}

		try
		{
			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingListing");
			var listing = await LoadStagedFilesAsync(agent);
			var packages = listing.Packages.ToList();
			StagingStatusLabel.Text = listing.Errors.Count > 0 && listing.Files.Count == 0
				? string.Format(L10n.Get("lblToolboxStagingFailed"), string.Join("; ", listing.Errors))
				: packages.Count == 0
					? L10n.Get("lblToolboxStagingEmpty")
					: string.Format(L10n.Get("lblToolboxStagingSummary"), packages.Count, InstalledApp.FormatSize(listing.PackageBytes));
		}
		catch (Exception ex)
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingFailed"), ex.Message);
		}
	}

	private async Task<DebugAgentStaging> LoadStagedFilesAsync(DebugAgentClient agent)
	{
		var listing = await agent.ListStagingAsync();
		var kept = L10n.Get("lblToolboxStagingKept");
		BindableLayout.SetItemsSource(StagedFileList, listing.Files.Select(f => new ToolboxStagedFileRow(f, kept, agent.SupportsStagedDelete)).ToList());
		ClearStagingBtn.IsEnabled = listing.Packages.Any();
		return listing;
	}

	/// <summary>
	/// Deletes the package files in the TV's staging folder, where every package this app ever pushed
	/// is still sitting. On request only: nothing in the install flow calls this.
	/// </summary>
	private async void OnClearStagingClicked(object? sender, EventArgs e)
	{
		var agent = _agent;
		if (agent is null)
		{
			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingNeedsAgent");
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingClearing");
			var result = await agent.ClearStagingAsync();
			var status = result.Failed.Count == 0
				? string.Format(L10n.Get("lblToolboxStagingCleared"), result.Deleted.Count, InstalledApp.FormatSize(result.FreedBytes))
				: string.Format(L10n.Get("lblToolboxStagingPartial"), result.Deleted.Count, result.Failed.Count, string.Join("; ", result.Failed));

			// Show what is left, but keep the verdict: the clear is the news here, not the listing.
			try { await LoadStagedFilesAsync(agent); }
			catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[toolbox] staging re-list after clear: {ex.Message}"); }
			StagingStatusLabel.Text = status;
		}
		catch (Exception ex)
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingFailed"), ex.Message);
		}
		finally
		{
			_busy = false;
		}
	}

	/// <summary>
	/// Deletes one staged file, package or not: the row's ✕. A clear only takes packages, so a .zip or
	/// an .xml another tool left behind can only go this way (#681).
	/// </summary>
	private async void OnDeleteStagedFileClicked(object? sender, EventArgs e)
	{
		if (sender is not Button button || button.CommandParameter is not ToolboxStagedFileRow row)
			return;

		var agent = _agent;
		if (agent is null)
		{
			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingNeedsAgent");
			return;
		}

		if (_busy)
			return;

		_busy = true;
		try
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingDeleting"), row.Name);
			await agent.DeleteStagedFileAsync(row.File);
			var status = string.Format(L10n.Get("lblToolboxStagingDeleted"), row.Name, InstalledApp.FormatSize(row.File.Size));

			try { await LoadStagedFilesAsync(agent); }
			catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[toolbox] staging re-list after delete: {ex.Message}"); }
			StagingStatusLabel.Text = status;
		}
		catch (Exception ex)
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingDeleteFailed"), row.Name, ex.Message);
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
	/// <summary>
	/// The agent's chip (#1041): attaches when there is no agent; once attached it offers what the
	/// agent itself does, Evaluate and Detach.
	/// </summary>
	private async void OnAgentChipClicked(object? sender, EventArgs e)
	{
		if (_agent is null)
		{
			OnAttachAgentClicked(sender, e);
			return;
		}

		var evaluate = L10n.Get("lblToolboxAgentEvaluate");
		var detach = L10n.Get("lblToolboxAgentDetach");
		var chosen = await DisplayActionSheet(AgentStatusLabel.Text, L10n.Get("lblCancel"), null, evaluate, detach);
		if (chosen == evaluate)
			await EvaluateAgentAsync();
		else if (chosen == detach)
			OnDetachAgentClicked(sender, e);
	}

	// The chip says whether the agent is there; attached it takes the accent, like the TV card's chips.
	private void ShowAgentChip(bool attached)
	{
		AgentChip.Text = L10n.Get(attached ? "lblToolboxAgentChipAttached" : "lblToolboxAgentChipAttach") + (attached ? "  ▾" : string.Empty);
		if (attached)
		{
			AgentChip.SetDynamicResource(Button.BackgroundColorProperty, "AccentSoftColor");
			AgentChip.SetDynamicResource(Button.TextColorProperty, "AccentColor");
		}
		else
		{
			AgentChip.SetDynamicResource(Button.BackgroundColorProperty, "FillColor");
			AgentChip.SetDynamicResource(Button.TextColorProperty, "InkColor");
		}
	}

	// Open an app shows either the known apps or, with the agent attached, the TV's own list.
	private void ShowAppList(bool agentList)
	{
		AppListSwitch.IsVisible = _agent is not null;
		agentList &= _agent is not null;
		KnownAppsSection.IsVisible = !agentList;
		AgentSection.IsVisible = agentList;
		KnownAppsSegment.BackgroundColor = agentList ? Colors.Transparent : Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("Surface");
		AgentAppsSegment.BackgroundColor = agentList ? Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("Surface") : Colors.Transparent;
	}

	private void OnAppListSwitchClicked(object? sender, EventArgs e) => ShowAppList(ReferenceEquals(sender, AgentAppsSegment));

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
		AgentChip.IsEnabled = false;
		StagingAttachBtn.IsEnabled = false;
		try
		{
			await DetachAgentAsync();

			var progress = new Progress<string>(key => SetAgentStatus(L10n.Get(key)));
			var agent = await DebugAgentClient.AttachCurrentAsync(
				_sdb, _tvIp, _installWgt,
				message => MainThread.BeginInvokeOnMainThread(() => SetAgentStatus(message)),
				progress);
			agent.Disconnected += OnAgentDisconnected;
			_agent = agent;

			var platform = await agent.PlatformAsync();
			_agentApps = await agent.ListAppsAsync();
			ApplyAgentFilter();

			ShowAgentChip(attached: true);
			ShowAppList(agentList: true);
			SetAgentStatus(string.Format(L10n.Get("lblToolboxAgentAttached"),
				agent.AgentVersion, _agentApps.Count, _agentApps.Count(a => !a.Show), platform.Tizen ?? "?"));

			// Read-only, and the one look at the folder that tells whether a clear is worth a tap.
			StagingNeedsAgent.IsVisible = false;
			StagingSection.IsVisible = true;
			await RefreshStagingAsync(agent);
		}
		catch (DebugAgentInstallException ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] agent install: {ex.Message}");
			SetAgentStatus(L10n.Get(ex.Key));
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
			AgentChip.IsEnabled = true;
			StagingAttachBtn.IsEnabled = true;
		}
	}

	// Hands the signing certificate to the TV-Store app on the TV (#844, phase 3): the profile this
	// head installs with, as PEM, over TV-Store's page in debug mode. Once armed, the TV signs and
	// installs catalog apps on its own. TV-Store has to be on the set already (installed from the
	// catalog like any package).
	private async void OnArmTvStoreClicked(object? sender, EventArgs e)
	{
		if (_sdb is null)
		{
			SetTvStoreStatus(L10n.Get("lblToolboxAgentNeedsSdb"));
			return;
		}

		if (_busy)
			return;

		_busy = true;
		ArmTvStoreBtn.IsEnabled = false;
		ShowTvStoreBtn.IsVisible = false;
		try
		{
			// This device's package-id renames (#839): a TV-Store installed under a new id is TV-Store too.
			var renames = MobileSettings.PackageIdRenamesJson;
			var state = await TvStorePackage.GetInstallStateAsync(_sdb, _tvIp, renames);
			if (state != TvStoreInstallState.Installed)
			{
				SetTvStoreStatus(L10n.Get(state == TvStoreInstallState.Outdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled"));
				// The message stays; one more button takes the user to TV-Store's catalog sheet (#888).
				_tvStoreEntry = TvStorePackage.FindInCatalog(CatalogStore.Releases);
				_tvStoreOutdated = state == TvStoreInstallState.Outdated;
				ShowTvStoreBtn.IsVisible = _tvStoreEntry is not null;
				return;
			}

			SetTvStoreStatus(L10n.Get("lblToolboxTvStoreCertificate"));
			var profile = _signingProfile is null
				? null
				: await _signingProfile(false, message => MainThread.BeginInvokeOnMainThread(() => SetTvStoreStatus(message)));
			if (profile is null)
			{
				SetTvStoreStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
				return;
			}
			// Every profile on this phone goes along, the covering one first (#44), so TV-Store can
			// update an app whichever of them installed it. The store is the profile's parent folder.
			var pems = SigningCertificatePem.OpenAll(profile, SigningCertificatePem.ProfilesIn(Path.GetDirectoryName(profile.ProfileDir)));

			var progress = new Progress<string>(key => SetTvStoreStatus(L10n.Get(key)));
			await using var store = await TvStoreClient.AttachAsync(_sdb, _tvIp, progress, renames);
			SetTvStoreStatus(L10n.Get("lblToolboxTvStoreArming"));
			await store.ArmAsync(pems);
			var status = await store.StatusAsync();
			// What is left to do depends on the Host PC IP the TV has right now (#960).
			var tv = await TizenDeveloperInfo.ReadAsync(new NetworkDevice { IpAddress = _tvIp });
			var next = TvStoreNextSteps.For(tv.DeveloperIP, status.OwnIp);
			SetTvStoreStatus(string.Format(L10n.Get(TvStoreNextSteps.MessageKey(next)), store.Version, status.OwnIp ?? _tvIp));
		}
		catch (OperationCanceledException)
		{
			// The certificate sheet was dismissed: nothing to hand over.
			SetTvStoreStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
		}
		catch (TvStoreUnreachableException ex)
		{
			// The listing failed, so TV-Store may well be there: point at Developer Mode, not the catalog (#889).
			System.Diagnostics.Trace.WriteLine($"[toolbox] arm TV-Store: no app listing: {ex.Reason}");
			SetTvStoreStatus(string.Format(L10n.Get("lblToolboxTvStoreUnreachable"), ex.Reason));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] arm TV-Store failed: {ex}");
			SetTvStoreStatus(string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message));
		}
		finally
		{
			_busy = false;
			ArmTvStoreBtn.IsEnabled = true;
		}
	}

	private void SetTvStoreStatus(string message) => TvStoreStatusLabel.Text = message;

	// ---- Send sources to TV (#1018, TV-Store#82) ----

	/// <summary>One signed repository in the list: ticked to go to the TV unless its key changed since (review it first).</summary>
	private sealed record TvSourceRow(UserSource Source, bool CanSend, CheckBox Check, Label Note);

	private readonly List<TvSourceRow> _tvSources = new();

	// The approved signed repositories of Your sources, ticked; one whose key changed is listed off with a note.
	private void ShowTvSources()
	{
		TvSourceRows.Clear();
		_tvSources.Clear();
		foreach (var source in UserSources.Parse(MobileSettings.UserSourcesJson).Where(UserSources.IsApprovedRepository))
		{
			var keyChanged = Apps2Samsung.Catalog.Repositories.SignedRepositories.LastLoad(source.Url)?.State
				== Apps2Samsung.Catalog.Repositories.RepositoryState.KeyChanged;
			var check = new CheckBox { IsChecked = !keyChanged, IsEnabled = !keyChanged, VerticalOptions = LayoutOptions.Center };
			var note = new Label
			{
				Text = keyChanged ? L10n.Get("lblToolboxSendSourcesReview") : string.Empty, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap,
				TextColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("Muted"),
			};
			note.IsVisible = !string.IsNullOrEmpty(note.Text);
			var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 4 };
			row.Add(check, 0, 0);
			row.Add(new VerticalStackLayout
			{
				Spacing = 1, VerticalOptions = LayoutOptions.Center,
				Children = { new Label { Text = UserSources.DisplayName(source), LineBreakMode = LineBreakMode.TailTruncation }, note },
			}, 1, 0);
			TvSourceRows.Add(row);
			_tvSources.Add(new TvSourceRow(source, !keyChanged, check, note));
		}
		NoTvSourcesLabel.IsVisible = _tvSources.Count == 0;
		TvSourceRows.IsVisible = SendSourcesBtn.IsVisible = _tvSources.Count > 0;
		SendSourcesBtn.IsEnabled = _sdb is not null;
	}

	/// <summary>
	/// Hands the ticked signed repositories to TV-Store with the keys approved here; the TV asks the
	/// person at it for each, and this waits for the answers (up to five minutes).
	/// </summary>
	private async void OnSendSourcesClicked(object? sender, EventArgs e)
	{
		if (_sdb is null)
		{
			SourcesStatusLabel.Text = L10n.Get("lblToolboxAgentNeedsSdb");
			return;
		}
		var chosen = _tvSources.Where(r => r.CanSend && r.Check.IsChecked).Select(r => r.Source).ToList();
		if (chosen.Count == 0)
		{
			SourcesStatusLabel.Text = L10n.Get("lblToolboxSendSourcesNone");
			return;
		}
		if (_busy)
			return;

		_busy = true;
		SendSourcesBtn.IsEnabled = false;
		try
		{
			var renames = MobileSettings.PackageIdRenamesJson;
			var progress = new Progress<string>(key => SourcesStatusLabel.Text = L10n.Get(key));
			await using var store = await TvStoreClient.AttachAsync(_sdb, _tvIp, progress, renames);
			SourcesStatusLabel.Text = L10n.Get("lblToolboxSendSourcesWaiting");
			var results = await store.AddSourcesAsync(chosen);
			foreach (var result in results)
			{
				var row = _tvSources.FirstOrDefault(r => string.Equals(r.Source.RepositoryId, result.RepositoryId, StringComparison.Ordinal));
				if (row is null)
					continue;
				row.Note.Text = result.Added ? L10n.Get("lblSourceOnTv") : result.Error ?? string.Empty;
				row.Note.IsVisible = !string.IsNullOrEmpty(row.Note.Text);
			}
			var lines = new List<string> { string.Format(L10n.Get("lblToolboxSendSourcesDone"), results.Count(r => r.Added), results.Count) };
			lines.AddRange(results.Where(r => !r.Added).Select(r => string.Format(L10n.Get("lblToolboxSendSourcesNotAdded"), r.Name ?? r.RepositoryId, r.Error)));
			SourcesStatusLabel.Text = string.Join(Environment.NewLine, lines);
		}
		catch (TvStoreNotInstalledException ex)
		{
			SourcesStatusLabel.Text = L10n.Get(ex.IsOutdated ? "lblToolboxTvStoreOutdated" : "lblToolboxTvStoreNotInstalled");
		}
		catch (TvStoreUnreachableException ex)
		{
			SourcesStatusLabel.Text = string.Format(L10n.Get("lblToolboxTvStoreUnreachable"), ex.Reason);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] send sources failed: {ex}");
			// Too old a TV-Store says so in its own words.
			SourcesStatusLabel.Text = ex is NotSupportedException ? ex.Message : string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message);
		}
		finally
		{
			_busy = false;
			SendSourcesBtn.IsEnabled = true;
		}
	}

	private void SetPackageStatus(string message) => PackageStatusLabel.Text = message;

	private void SetPackageBusy(bool busy)
	{
		_busy = busy;
		SignPackageBtn.IsEnabled = !busy && _sdb is not null && _pickPackage is not null && _signingProfile is not null;
		SendToTvStoreBtn.IsEnabled = !busy && _pickPackage is not null;
	}

	/// <summary>
	/// Sign a package (#979): a .wgt/.tpk from the phone, signed with the certificate that covers this
	/// TV (made first when none does, the same sheet as an install) and handed to the share sheet,
	/// without installing it. For tools that install another way. The picked file is a cache copy,
	/// so the user's own file is never changed.
	/// </summary>
	private async void OnSignPackageClicked(object? sender, EventArgs e)
	{
		if (_busy || _pickPackage is null || _signingProfile is null)
			return;
		if (_sdb is null)
		{
			SetPackageStatus(L10n.Get("lblToolboxAgentNeedsSdb"));
			return;
		}

		var input = await _pickPackage();
		if (input is null)
			return;
		if (!PackageSigner.IsPackage(input))
		{
			SetPackageStatus(L10n.Get("statusChooseWgt"));
			return;
		}

		SetPackageBusy(true);
		try
		{
			// Partner when the package needs it, as an install would.
			var needsPartner = WgtPrivileges.RequiresPartner(input);
			SetPackageStatus(L10n.Get("lblToolboxTvStoreCertificate"));
			var profile = await _signingProfile(needsPartner, message => MainThread.BeginInvokeOnMainThread(() => SetPackageStatus(message)));
			if (profile is null)
			{
				SetPackageStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
				return;
			}

			var output = Path.Combine(FileSystem.CacheDirectory, "signed", PackageSigner.SuggestedOutputName(input));
			SetPackageStatus(string.Format(L10n.Get("lblToolboxPackageSigning"), Path.GetFileName(input)));
			var signed = await PackageSigner.SignCopyAsync(_sdb, input, output, profile,
				line => MainThread.BeginInvokeOnMainThread(() => SetPackageStatus(ProgressText.Localize(line, L10n.Get))));
			SetPackageStatus(string.Format(L10n.Get("lblToolboxPackageSigned"), Path.GetFileName(input), _tvLabel, Path.GetFileName(signed.Path)));

			// The phone has no Save as: the share sheet puts the signed copy where it is wanted
			// (Files, a PC, another app).
			await Share.Default.RequestAsync(new ShareFileRequest(L10n.Get("lblSignedPackage"), new ShareFile(signed.Path)));
		}
		catch (OperationCanceledException)
		{
			// The certificate sheet was cancelled.
			SetPackageStatus(L10n.Get("lblToolboxTvStoreNoCertificate"));
		}
		catch (CertificateNotYetValidException ex)
		{
			SetPackageStatus(ex.Message);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] sign package failed: {ex}");
			SetPackageStatus(string.Format(L10n.Get("lblToolboxPackageSignFailed"), ex.Message));
		}
		finally
		{
			try { File.Delete(input); } catch { /* the cache copy; best-effort */ }
			SetPackageBusy(false);
		}
	}

	/// <summary>
	/// Send to TV-Store (#979): a .wgt/.tpk from the phone goes to TV-Store's upload page on the TV,
	/// customized as an install would (the values asked before install) but not signed; TV-Store is
	/// opened on the TV when it is not running, asks on the screen and signs it itself. No developer
	/// connection from this phone is involved, so it works while Host PC IP is 127.0.0.1.
	/// </summary>
	private async void OnSendToTvStoreClicked(object? sender, EventArgs e)
	{
		if (_busy || _pickPackage is null)
			return;

		var input = await _pickPackage();
		if (input is null)
			return;
		if (!PackageSigner.IsPackage(input))
		{
			SetPackageStatus(L10n.Get("statusChooseWgt"));
			return;
		}

		SetPackageBusy(true);
		try
		{
			Action<string> report = line => MainThread.BeginInvokeOnMainThread(() => SetPackageStatus(ProgressText.Localize(line, L10n.Get)));
			if (!await TvStoreHandoff.EnsureRunningAsync(_tvIp, report))
			{
				SetPackageStatus(L10n.Get("tvStoreNotRunning"));
				return;
			}
			// The picked file is the cache copy, so the customization may edit it in place.
			if (_customize is not null)
				await _customize(_tvIp, input, report);
			var sent = await TvStoreHandoff.SendAsync(_tvIp, input, report);
			SetPackageStatus(string.Format(L10n.Get(sent.TvStoreOpened ? "tvStoreSentBody" : "tvStoreSentBodyClosed"), sent.Upload.Name));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] send to TV-Store failed: {ex}");
			SetPackageStatus(string.Format(L10n.Get("lblToolboxTvStoreFailed"), ex.Message));
		}
		finally
		{
			try { File.Delete(input); } catch { /* the cache copy; best-effort */ }
			SetPackageBusy(false);
		}
	}

	// TV-Store's catalog entry and whether the TV runs an old one, from the last Arm attempt (#888).
	private GitHubRelease? _tvStoreEntry;
	private bool _tvStoreOutdated;

	// Leaves the toolbox (it sits on the Install tab's stack) and opens TV-Store's sheet there, as
	// an Update when the set has an old one. Install, then come back and arm.
	private async void OnShowTvStoreClicked(object? sender, EventArgs e)
	{
		if (_tvStoreEntry is null || _busy)
			return;
		var request = new CatalogOpenRequest(_tvStoreEntry, null, _tvStoreOutdated);
		await Navigation.PopAsync();
		await CatalogStore.OpenOnInstallTabAsync(request);
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
		ShowAgentChip(attached: false);
		ShowAppList(agentList: false);
		BindableLayout.SetItemsSource(StagedFileList, Array.Empty<ToolboxStagedFileRow>());
		StagingSection.IsVisible = false;
		StagingNeedsAgent.IsVisible = true;
		ClearStagingBtn.IsEnabled = false;
		StagingStatusLabel.Text = string.Empty;

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

	// The last expression, offered again: these get tweaked and rerun.
	private string _lastExpression = string.Empty;

	/// <summary>Asks for an expression, runs it inside the agent and shows what came back, verbatim.</summary>
	private async Task EvaluateAgentAsync()
	{
		var agent = _agent;
		if (agent is null)
		{
			SetAgentStatus(L10n.Get("lblToolboxAgentNotAttached"));
			return;
		}

		var expression = (await DisplayPromptAsync(L10n.Get("lblToolboxAgentEvaluate"), L10n.Get("lblToolboxAgentEvaluateHint"),
			L10n.Get("lblToolboxAgentEvaluate"), L10n.Get("lblCancel"),
			L10n.Get("lblToolboxAgentEvaluatePlaceholder"), initialValue: _lastExpression))?.Trim();
		if (string.IsNullOrEmpty(expression))
			return;
		_lastExpression = expression;

		string result;
		try
		{
			result = await agent.EvaluateAsync(expression);
		}
		catch (Exception ex)
		{
			result = ex.Message;
		}
		await DisplayAlert(expression, result, L10n.Get("lblOk"));
	}

	private void SetAgentStatus(string message)
	{
		AgentStatusLabel.Text = message;
		AgentStatusLabel.IsVisible = !string.IsNullOrEmpty(message);
	}

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
		// "KEY_MUTE" reads as "MUTE", the label on the physical remote, which is what the manuals
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

/// <summary>One file in the TV's install staging folder, as the agent listed it.</summary>
public sealed class ToolboxStagedFileRow
{
	public ToolboxStagedFileRow(DebugAgentStagedFile file, string keptLabel, bool canDelete)
	{
		File = file;
		CanDelete = canDelete;
		var detail = InstalledApp.FormatSize(file.Size);
		if (file.Modified is { } modified)
			detail += $" · {modified.LocalDateTime:g}";
		// A file a clear leaves alone (not a package) says so, so the list and the count agree.
		if (!file.IsPackage)
			detail += $" · {keptLabel}";
		Detail = detail;
	}

	public DebugAgentStagedFile File { get; }
	public string Name => File.Name;
	public string Detail { get; }

	/// <summary>Whether the agent on the TV can delete this one file (v0.5.0 and later).</summary>
	public bool CanDelete { get; }
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
