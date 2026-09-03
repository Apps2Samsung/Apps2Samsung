using Apps2Samsung.Catalog;
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
/// addresses them and they go over SDB where the set has Developer Mode on (#641).
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

	// The full list behind the filtered one shown.
	private IReadOnlyList<SamsungRemoteLaunchTarget> _targets = Array.Empty<SamsungRemoteLaunchTarget>();

	// One action at a time: a second combination sent on top of a running one would interleave its
	// presses with the first one's, which is not a combination the TV was ever shown.
	private bool _busy;

	public TvToolboxPage(ISdbEngine? sdb, string tvIp, string tvLabel)
	{
		InitializeComponent();
		_tvIp = tvIp;
		_tvLabel = tvLabel;
		_sdb = sdb;

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

	private async void OnLaunchSystemAppClicked(object? sender, EventArgs e)
	{
		if (sender is Button { BindingContext: SamsungSystemAppRow row })
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
				_ => string.Format(L10n.Get("lblToolboxLaunchFailed"), target.Name),
			});
		}
		finally
		{
			_busy = false;
		}
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
