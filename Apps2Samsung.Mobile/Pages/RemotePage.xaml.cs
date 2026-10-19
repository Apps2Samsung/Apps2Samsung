using Apps2Samsung.Mobile.Controls;
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
		ShowMode(media: false);
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_tv.Changed -= OnTvChanged;
		_tv.Changed += OnTvChanged;
		await ConnectToSelectedAsync();
	}

	protected override async void OnDisappearing()
	{
		base.OnDisappearing();
		_tv.Changed -= OnTvChanged;
		_typingCts?.Cancel();
		_typingCts = null;
		await DropConnectionAsync();
	}

	// The TV comes from the shared selection (the ▾ on any tab); the remote needs only a TV on the
	// network, Developer Mode or not.
	private async Task ConnectToSelectedAsync()
	{
		TopBar.Title = _tv.HasTv ? _tv.DisplayName : L10n.Get("lblChooseTv");
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"), StatusKind.Warn);
			return;
		}
		_tvIp = _tv.Ip!;
		await ConnectAsync();
	}

	private async Task DropConnectionAsync()
	{
		var remote = _remote;
		_remote = null;
		if (remote is not null)
			await remote.DisposeAsync();
	}

	// Another TV picked with the ▾: let go of this one and connect to that.
	private async void OnTvChanged()
	{
		if (_tv.Ip == _tvIp && _tv.HasTv)
			return;
		await DropConnectionAsync();
		_tvIp = string.Empty;
		await ConnectToSelectedAsync();
	}

	private async void OnChangeTvClicked(object? sender, EventArgs e) => await TvPicker.ChooseAsync(this, _tv);

	// Power doubles as Wake: with the channel up it is the TV's power key (which switches it off);
	// without one it is the fresh connect that wakes a set in standby.
	private async void OnPowerTapped(object? sender, EventArgs e)
	{
		if (_remote is { IsConnected: true })
			await SendKeyAsync(SamsungRemoteKeys.Power);
		else
			await WakeAsync();
	}

	private void OnNavTabClicked(object? sender, EventArgs e) => ShowMode(media: false);

	private void OnMediaTabClicked(object? sender, EventArgs e) => ShowMode(media: true);

	// The Navigation | Media switch: the chosen half on the surface colour, the other transparent.
	private void ShowMode(bool media)
	{
		NavPanel.IsVisible = !media;
		MediaPanel.IsVisible = media;
		NavTab.BackgroundColor = media ? Colors.Transparent : DesignTheme.ColorOf("Surface");
		MediaTab.BackgroundColor = media ? DesignTheme.ColorOf("Surface") : Colors.Transparent;
	}

	private void OnKeyboardClicked(object? sender, EventArgs e)
	{
		KeyboardSheet.IsVisible = true;
		TextEntry.Focus();
	}

	private void OnKeyboardCloseClicked(object? sender, EventArgs e)
	{
		TextEntry.Unfocus();
		KeyboardSheet.IsVisible = false;
	}

	private void OnKeyboardDimTapped(object? sender, TappedEventArgs e) => OnKeyboardCloseClicked(sender, e);

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
			var progress = new Progress<string>(key => SetStatus(L10n.Get(key), StatusKind.Busy));
			var session = await RemoteSession.ConnectAsync(_tvIp, RemoteCredentials.Instance, progress);

			if (!session.Connected)
			{
				SetStatus(L10n.Get(RemoteCredentials.StatusKeyFor(session.Outcome)), StatusKind.Bad);
				return;
			}

			_remote = session.Client;
			// Nothing is known about what sits in the TV's text field on a new connection, so start from
			// "unmirrored" and let the first keystroke transmit in full.
			_mirroredText = string.Empty;
			SetStatus(L10n.Get("lblRemoteConnected"), StatusKind.Ok);
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
	private async Task WakeAsync()
	{
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"), StatusKind.Warn);
			return;
		}
		if (_remote is { IsConnected: true })
		{
			SetStatus(L10n.Get("lblRemoteConnected"), StatusKind.Ok);
			return;
		}

		var stale = _remote;
		_remote = null;
		if (stale is not null)
			await stale.DisposeAsync();
		_tvIp = _tv.Ip!;
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


	// The status goes under the TV's name in the top bar, and on the keyboard sheet while it is open
	// (that is where "On the TV: …" matters).
	private void SetStatus(string message, StatusKind kind)
	{
		TopBar.Subtitle = message;
		TopBar.Status = kind switch
		{
			StatusKind.Busy => TopBarStatus.Busy,
			StatusKind.Ok => TopBarStatus.Ok,
			StatusKind.Warn => TopBarStatus.Warn,
			_ => TopBarStatus.Bad,
		};
		if (KeyboardSheet.IsVisible)
			TextHintLabel.Text = message;
	}
}
