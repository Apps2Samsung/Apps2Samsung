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

	public RemotePage(string tvIp, string tvLabel)
	{
		InitializeComponent();
		_tvIp = tvIp;
		_tvLabel = tvLabel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await ConnectAsync();
	}

	protected override async void OnDisappearing()
	{
		base.OnDisappearing();
		var remote = _remote;
		_remote = null;
		if (remote is not null)
			await remote.DisposeAsync();
	}

	private async Task ConnectAsync()
	{
		SetStatus($"{_tvLabel} — connecting…");

		var capability = await SamsungRemoteClient.ProbeAsync(_tvIp);
		if (!capability.Supported)
		{
			// The remote API lives on port 8001/8002, which a TV in standby doesn't serve at all —
			// so "no answer" most often means "asleep", not "unsupported".
			SetStatus($"{_tvLabel} — no remote API. Is the TV on and on this network?");
			return;
		}

		if (!capability.IsAwake)
		{
			SetStatus($"{_tvLabel} — the TV reports standby. Turn it on with the physical remote first.");
			return;
		}

		var stored = MobileSettings.GetRemoteToken(_tvIp);
		var client = new SamsungRemoteClient(_tvIp, token: stored, secure: capability.UsesToken);
		client.TokenIssued += token => MobileSettings.SetRemoteToken(_tvIp, token);

		// No stored token on a token-auth set means the TV is about to prompt — say so, then give the
		// user time to walk over and accept it.
		if (capability.UsesToken && string.IsNullOrEmpty(stored))
			SetStatus($"{_tvLabel} — accept the prompt on the TV to pair.");

		var timeout = capability.UsesToken && string.IsNullOrEmpty(stored)
			? TimeSpan.FromSeconds(60)
			: TimeSpan.FromSeconds(10);

		using var cts = new CancellationTokenSource(timeout);
		if (!await client.ConnectAsync(cts.Token))
		{
			await client.DisposeAsync();
			SetStatus(capability.UsesToken && string.IsNullOrEmpty(stored)
				? $"{_tvLabel} — pairing wasn't accepted. Reopen this page to try again."
				: $"{_tvLabel} — couldn't open the remote channel.");
			return;
		}

		_remote = client;
		var name = string.IsNullOrWhiteSpace(capability.Name) ? _tvLabel : capability.Name;
		SetStatus($"{name} — connected");
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

	private async void OnSendTextClicked(object? sender, EventArgs e)
	{
		var text = TextEntry.Text;
		if (string.IsNullOrEmpty(text))
			return;

		var remote = _remote;
		if (remote is null)
		{
			SetStatus("Not connected — reopen this page.");
			return;
		}

		if (await remote.SendTextAsync(text))
		{
			TextEntry.Text = string.Empty;
			SetStatus($"Sent \"{text}\"");
		}
		else
		{
			SetStatus("The TV didn't take the text. Is a text field focused?");
		}
	}

	private async Task<bool> SendKeyAsync(string key)
	{
		var remote = _remote;
		if (remote is null)
		{
			SetStatus("Not connected — reopen this page.");
			return false;
		}

		if (await remote.SendKeyAsync(key))
			return true;

		// The client reconnects on its own, so a single miss is worth reporting but not fatal.
		SetStatus("That press didn't get through — the TV may have gone to standby.");
		return false;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void SetStatus(string message) => StatusLabel.Text = message;
}
