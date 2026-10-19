using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
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
	private string _tvIp = string.Empty;
	private string _tvLabel = string.Empty;
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

	private readonly TvSelection _tv;
	// One connection attempt at a time: Wake while a connect is still waking or pairing would open
	// a second socket next to the first.
	private bool _connecting;

	/// <summary>What the status notice shows next to its text.</summary>
	private enum StatusKind { Busy, Ok, Warn, Bad }

	public RemotePage(TvSelection tv)
	{
		InitializeComponent();
		_tv = tv;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		// The TV comes from the Install tab's picker; the remote needs only a TV on the network.
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"), StatusKind.Warn);
			return;
		}
		_tvIp = _tv.Ip!;
		_tvLabel = _tv.Label;
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

	/// <summary>
	/// Opens the channel: probe, wake the set if it is asleep and we know its MAC, pair on first use.
	/// The sequence itself lives in Core (<see cref="RemoteSession"/>), shared with the desktop head
	/// and with the TV toolbox.
	/// </summary>
	private async Task ConnectAsync()
	{
		if (_connecting)
			return;
		_connecting = true;
		try
		{
			var progress = new Progress<string>(key => SetStatus($"{_tvLabel} — {L10n.Get(key)}", StatusKind.Busy));
			var session = await RemoteSession.ConnectAsync(_tvIp, RemoteCredentials.Instance, progress);

			if (!session.Connected)
			{
				SetStatus($"{_tvLabel} — {L10n.Get(RemoteCredentials.StatusKeyFor(session.Outcome))}", StatusKind.Bad);
				return;
			}

			_remote = session.Client;
			// Nothing is known about what sits in the TV's text field on a new connection, so start from
			// "unmirrored" and let the first keystroke transmit in full.
			_mirroredText = string.Empty;
			var name = string.IsNullOrWhiteSpace(session.TvName) ? _tvLabel : session.TvName;
			SetStatus($"{name} — {L10n.Get("lblRemoteConnected")}", StatusKind.Ok);
		}
		finally
		{
			_connecting = false;
		}
	}

	/// <summary>
	/// Wake: a set in standby serves no remote channel, so the only way back is a fresh connect, which
	/// sends the magic packet when the probe finds the TV asleep (<see cref="RemoteSession"/>). Also the
	/// reconnect after a refused pairing or a dropped link. With the channel up the TV is awake already.
	/// </summary>
	private async void OnWakeClicked(object? sender, EventArgs e)
	{
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"), StatusKind.Warn);
			return;
		}
		if (_remote is { IsConnected: true })
		{
			SetStatus($"{_tvLabel} — {L10n.Get("lblRemoteConnected")}", StatusKind.Ok);
			return;
		}

		var stale = _remote;
		_remote = null;
		if (stale is not null)
			await stale.DisposeAsync();
		_tvIp = _tv.Ip!;
		_tvLabel = _tv.Label;
		await ConnectAsync();
	}

	private void OnNumpadToggleClicked(object? sender, EventArgs e)
	{
		Numpad.IsVisible = !Numpad.IsVisible;
		NumpadToggle.BackgroundColor = DesignTheme.ColorOf(Numpad.IsVisible ? "AccentSoft" : "Surface");
		NumpadToggle.TextColor = DesignTheme.ColorOf(Numpad.IsVisible ? "Accent" : "Ink");
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
				SetStatus(L10n.Get("lblRemoteNotConnected"), StatusKind.Warn);
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
					: string.Format(L10n.Get("lblRemoteTextMirrored"), text), StatusKind.Ok);
			}
			else
			{
				SetStatus(L10n.Get("lblRemoteTextFailed"), StatusKind.Warn);
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
			SetStatus(L10n.Get("lblRemoteNotConnected"), StatusKind.Warn);
			return;
		}

		if (await remote.SendTextAsync(text))
			_mirroredText = text;
		else
			SetStatus(L10n.Get("lblRemoteTextFailed"), StatusKind.Warn);
	}

	private async Task<bool> SendKeyAsync(string key)
	{
		var remote = _remote;
		if (remote is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"), StatusKind.Warn);
			return false;
		}

		if (await remote.SendKeyAsync(key))
			return true;

		// The client reconnects on its own, so a single miss is worth reporting but not fatal.
		SetStatus(L10n.Get("lblRemoteKeyFailed"), StatusKind.Warn);
		return false;
	}


	private void SetStatus(string message, StatusKind kind)
	{
		StatusLabel.Text = message;
		var busy = kind == StatusKind.Busy;
		StatusSpinner.IsVisible = busy;
		StatusSpinner.IsRunning = busy;
		StatusDot.IsVisible = !busy;
		StatusDot.Fill = new SolidColorBrush(DesignTheme.ColorOf(kind switch
		{
			StatusKind.Ok => "Ok",
			StatusKind.Warn => "Warn",
			_ => "Bad",
		}));
	}
}
