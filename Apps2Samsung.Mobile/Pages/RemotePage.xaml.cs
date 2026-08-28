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

	private async void OnSendTextClicked(object? sender, EventArgs e)
	{
		var text = TextEntry.Text;
		if (string.IsNullOrEmpty(text))
			return;

		var remote = _remote;
		if (remote is null)
		{
			SetStatus(L10n.Get("lblRemoteNotConnected"));
			return;
		}

		if (await remote.SendTextAsync(text))
		{
			TextEntry.Text = string.Empty;
			SetStatus(string.Format(L10n.Get("lblRemoteTextSentValue"), text));
		}
		else
		{
			SetStatus(L10n.Get("lblRemoteTextFailed"));
		}
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

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private void SetStatus(string message) => StatusLabel.Text = message;
}
