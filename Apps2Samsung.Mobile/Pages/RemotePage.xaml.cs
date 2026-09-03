using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Turns the phone into a remote for the TV the installer already found (#544). This talks to the
/// TV's <c>samsung.remote.control</c> WebSocket channel, not to SDB: it needs no Developer Mode, so
/// it works on any Samsung TV on the network — but the TV must be awake, and the first connection
/// makes it show an "allow this device?" prompt that has to be accepted with the physical remote.
/// The token that follows is stored per TV (<see cref="MobileSettings.GetRemoteToken"/>), so later
/// sessions connect silently.
/// </summary>
public partial class RemotePage : ContentPage
{
	private readonly string _tvIp;
	private readonly string _tvLabel;
	private SamsungRemoteClient? _remote;
	// Play/pause: newer sets take the single toggle, older ones only the separate keys. Once a
	// toggle press is refused we stop trying it and alternate Play/Pause for the rest of the session.
	private bool _toggleUnsupported;
	private bool _lastWasPlay;

	// Live typing. SendInputString hands the TV the whole field contents rather than appending to
	// them, so the mirror is simply "send the box as it stands" - which makes deletions work for
	// free, a shorter string overwriting a longer one. Sending on every keystroke would put one
	// WebSocket round trip on each letter, so a keystroke only supersedes the pending send and the
	// text goes out once typing pauses.
	private static readonly TimeSpan TypingDebounce = TimeSpan.FromMilliseconds(180);
	private CancellationTokenSource? _typingCts;
	// What the TV was last told, so a value that comes back round to the mirrored one inside a single
	// debounce window (type a letter, delete it) costs nothing.
	private string _mirroredText = string.Empty;

	// The toolbox does one thing at a time: a second sequence sent on top of a running one would
	// interleave its presses with the first one's, which is not a combination the TV was ever shown.
	private bool _toolboxBusy;

	public RemotePage(string tvIp, string tvLabel)
	{
		InitializeComponent();
		_tvIp = tvIp;
		_tvLabel = tvLabel;

		// Only the combinations this channel can deliver; the standby ones are covered by the note
		// under the slider (see SamsungRemoteSequences).
		BindableLayout.SetItemsSource(
			SequenceList,
			SamsungRemoteSequences.Sendable.Select(s => new ToolboxSequenceRow(s)).ToList());
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await ConnectAsync();
	}

	protected override async void OnDisappearing()
	{
		base.OnDisappearing();
		_typingCts?.Cancel();
		_typingCts = null;
		var remote = _remote;
		_remote = null;
		if (remote is not null)
			await remote.DisposeAsync();
	}

	private async Task ConnectAsync()
	{
		SetStatus($"{_tvLabel} — {L10n.Get("lblRemoteConnecting")}");

		var capability = await SamsungRemoteClient.ProbeAsync(_tvIp);

		// A sleeping TV serves neither the REST API nor the remote channel, so "no answer" and
		// "standby" are the same situation: nothing works until the set is woken. If we cached its
		// MAC while it was awake, we can do that ourselves.
		if (!capability.Supported || !capability.IsAwake)
		{
			capability = await TryWakeAsync(capability);
			if (!capability.Supported || !capability.IsAwake)
				return;
		}

		// Remember the MAC while we can read it — a sleeping TV won't tell us later.
		if (!string.IsNullOrEmpty(capability.MacAddress))
			MobileSettings.SetRemoteMac(_tvIp, capability.MacAddress);

		var stored = MobileSettings.GetRemoteToken(_tvIp);
		var client = new SamsungRemoteClient(_tvIp, token: stored, secure: capability.UsesToken);
		client.TokenIssued += token => MobileSettings.SetRemoteToken(_tvIp, token);

		// No stored token on a token-auth set means the TV is about to prompt — say so, then give the
		// user time to walk over and accept it.
		if (capability.UsesToken && string.IsNullOrEmpty(stored))
			SetStatus($"{_tvLabel} — {L10n.Get("lblRemotePairPrompt")}");

		var timeout = capability.UsesToken && string.IsNullOrEmpty(stored)
			? TimeSpan.FromSeconds(60)
			: TimeSpan.FromSeconds(10);

		using var cts = new CancellationTokenSource(timeout);
		if (!await client.ConnectAsync(cts.Token))
		{
			await client.DisposeAsync();
			SetStatus($"{_tvLabel} — " + (capability.UsesToken && string.IsNullOrEmpty(stored)
				? L10n.Get("lblRemotePairFailed")
				: L10n.Get("lblRemoteNoChannel")));
			return;
		}

		_remote = client;
		// Nothing is known about what sits in the TV's text field on a new connection, so start from
		// "unmirrored" and let the first keystroke transmit in full.
		_mirroredText = string.Empty;
		var name = string.IsNullOrWhiteSpace(capability.Name) ? _tvLabel : capability.Name;
		SetStatus($"{name} — {L10n.Get("lblRemoteConnected")}");
	}

	/// <summary>
	/// Wakes the TV with a magic packet, if we know its MAC, and waits for it to come up. Returns the
	/// re-probed capability, and sets the status when it couldn't be done — the caller only continues
	/// when the TV is actually awake.
	/// </summary>
	private async Task<SamsungRemoteCapability> TryWakeAsync(SamsungRemoteCapability capability)
	{
		var mac = MobileSettings.GetRemoteMac(_tvIp);
		if (string.IsNullOrEmpty(mac))
		{
			// Never seen this TV awake, so there is no MAC to wake it with.
			SetStatus($"{_tvLabel} — {L10n.Get("lblRemoteNoAnswerNoMac")}");
			return capability;
		}

		SetStatus($"{_tvLabel} — {L10n.Get("lblRemoteWaking")}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		if (await SamsungRemoteWake.WakeAndWaitAsync(_tvIp, mac, TimeSpan.FromSeconds(40), cts.Token))
			return await SamsungRemoteClient.ProbeAsync(_tvIp);

		// Wake-on-LAN needs the TV's own network-standby setting on, and a LAN that passes broadcast.
		SetStatus($"{_tvLabel} — {L10n.Get("lblRemoteWakeFailed")}");
		return capability;
	}

	private async void OnKeyClicked(object? sender, EventArgs e)
	{
		if (sender is not Button button || button.CommandParameter is not string key)
			return;

		await SendKeyAsync(key);
	}

	// Play/pause with a fallback for sets that don't implement the toggle.
	private async void OnPlayPauseClicked(object? sender, EventArgs e)
	{
		if (!_toggleUnsupported)
		{
			if (await SendKeyAsync(SamsungRemoteKeys.PlayPause))
				return;
			_toggleUnsupported = true;
		}

		_lastWasPlay = !_lastWasPlay;
		await SendKeyAsync(_lastWasPlay ? SamsungRemoteKeys.Play : SamsungRemoteKeys.Pause);
	}

	private void OnTextChanged(object? sender, TextChangedEventArgs e)
	{
		// Supersede whatever was queued: only the newest value is worth sending, and cancelling the
		// older one also stops two sends racing to arrive out of order on a slow link.
		_typingCts?.Cancel();
		var cts = new CancellationTokenSource();
		_typingCts = cts;
		_ = MirrorTextAsync(e.NewTextValue ?? string.Empty, cts);
	}

	/// <summary>
	/// Waits out the current burst of typing and then puts <paramref name="text"/> on the TV. Runs on
	/// the UI thread throughout - the awaits here deliberately keep their context, so touching
	/// <see cref="SetStatus"/> afterwards is safe.
	/// </summary>
	private async Task MirrorTextAsync(string text, CancellationTokenSource cts)
	{
		try
		{
			await Task.Delay(TypingDebounce, cts.Token);

			if (text == _mirroredText)
				return;

			var remote = _remote;
			if (remote is null)
			{
				SetStatus(L10n.Get("lblRemoteNotConnected"));
				return;
			}

			// Deliberately not handed the debounce token: SamsungRemoteClient reads any exception out
			// of a send - a cancellation as much as a dropped Wi-Fi link - as a dead socket and tears
			// the connection down, so cancelling a keystroke mid-flight would cost a reconnect. Once a
			// send has started it is left to finish.
			var sent = await remote.SendTextAsync(text);

			// Superseded while that was in flight: the newer send owns the mirror and the status line,
			// and stamping this older value over it would leave the two out of step.
			if (cts.IsCancellationRequested)
				return;

			if (sent)
			{
				_mirroredText = text;
				SetStatus(text.Length == 0
					? L10n.Get("lblRemoteTextCleared")
					: string.Format(L10n.Get("lblRemoteTextMirrored"), text));
			}
			else
			{
				SetStatus(L10n.Get("lblRemoteTextFailed"));
			}
		}
		catch (OperationCanceledException)
		{
			// A newer keystroke took over before this one went out.
		}
		finally
		{
			if (ReferenceEquals(_typingCts, cts))
				_typingCts = null;
			cts.Dispose();
		}
	}

	// The text is on the TV already, so committing it is all that is left: flush anything still
	// waiting out its debounce, then press Enter the way the on-screen keyboard's Done key would.
	private async void OnSubmitClicked(object? sender, EventArgs e)
	{
		await FlushTypingAsync();
		await SendKeyAsync(SamsungRemoteKeys.Enter);
	}

	/// <summary>Puts the box on the TV right now, without waiting for the debounce to elapse.</summary>
	private async Task FlushTypingAsync()
	{
		_typingCts?.Cancel();
		_typingCts = null;

		var text = TextEntry.Text ?? string.Empty;
		if (text == _mirroredText)
			return;

		var remote = _remote;
		if (remote is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (await remote.SendTextAsync(text))
			_mirroredText = text;
		else
			SetStatus(L10n.Get("lblRemoteTextFailed"));
	}

	private async Task<bool> SendKeyAsync(string key)
	{
		var remote = _remote;
		if (remote is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"));
			return false;
		}

		if (await remote.SendKeyAsync(key))
			return true;

		// The client reconnects on its own, so a single miss is worth reporting but not fatal.
		SetStatus(L10n.Get("lblRemoteKeyFailed"));
		return false;
	}

	// ---- TV toolbox (#635) ----

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
			SetToolboxStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (_toolboxBusy)
			return;

		_toolboxBusy = true;
		try
		{
			var total = row.Sequence.Keys.Count;
			var progress = new Progress<SamsungRemoteKeyDelivery>(d =>
				SetToolboxStatus(string.Format(L10n.Get("lblToolboxSending"), row.Name, d.Index + 1, total)));

			var result = await SamsungRemoteSequences.SendAsync(
				remote, row.Sequence, (int)Math.Round(GapSlider.Value), progress);

			SetToolboxStatus(result.Completed
				? string.Format(L10n.Get("lblToolboxSeqDelivered"), row.Name, total)
				: string.Format(L10n.Get("lblToolboxSeqStopped"), row.Name, result.DeliveredCount + 1, total));
		}
		finally
		{
			_toolboxBusy = false;
		}
	}

	/// <summary>
	/// Fills the launcher list. The TV is asked what it has installed; whether it answers or not, the
	/// community catalogue is merged in, which is what makes an app the launcher hides reachable.
	/// </summary>
	private async void OnLoadAppsClicked(object? sender, EventArgs e)
	{
		var remote = _remote;
		if (remote is null)
		{
			SetToolboxStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (_toolboxBusy)
			return;

		_toolboxBusy = true;
		try
		{
			SetToolboxStatus(L10n.Get("lblToolboxAppsLoading"));
			var targets = await SamsungRemoteApps.BuildLauncherListAsync(remote);

			BindableLayout.SetItemsSource(ToolboxAppList, targets.Select(t => new ToolboxAppRow(t)).ToList());

			var status = string.Format(L10n.Get("lblToolboxAppsCount"), targets.Count, targets.Count(t => t.ReportedByTv));
			if (SamsungTvAppCatalog.IsOffline)
				status += " " + L10n.Get("lblToolboxAppsOffline");
			SetToolboxStatus(status);
		}
		finally
		{
			_toolboxBusy = false;
		}
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

		// No name to fall back on, so the DIAL attempt is out; the other two paths take an ID.
		await LaunchAsync(new SamsungRemoteLaunchTarget(id, id, IconUrl: null, AppType: 0, ReportedByTv: false));
	}

	private async Task LaunchAsync(SamsungRemoteLaunchTarget target)
	{
		var remote = _remote;
		if (remote is null)
		{
			SetToolboxStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (_toolboxBusy)
			return;

		_toolboxBusy = true;
		try
		{
			SetToolboxStatus(string.Format(L10n.Get("lblToolboxLaunching"), target.Name));
			var result = await SamsungRemoteApps.LaunchAsync(remote, _tvIp, target);

			SetToolboxStatus(result switch
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
			_toolboxBusy = false;
		}
	}

	private void SetToolboxStatus(string message)
	{
		ToolboxStatusLabel.Text = message;
		ToolboxStatusLabel.IsVisible = true;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void SetStatus(string message) => StatusLabel.Text = message;
}

/// <summary>One row of the toolbox's sequence list, with the Core sequence's keys spelled out.</summary>
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

/// <summary>One row of the toolbox's launcher list.</summary>
public sealed class ToolboxAppRow
{
	public ToolboxAppRow(SamsungRemoteLaunchTarget target)
	{
		Target = target;
		Origin = target.ReportedByTv ? string.Empty : L10n.Get("lblToolboxNotOnTv");
	}

	public SamsungRemoteLaunchTarget Target { get; }
	public string Name => Target.Name;
	public string AppId => Target.AppId;
	public string? IconUrl => Target.IconUrl;

	/// <summary>Empty for an app the TV listed; otherwise the "not listed by the TV" note.</summary>
	public string Origin { get; }
	public bool HasOrigin => !string.IsNullOrEmpty(Origin);
}
