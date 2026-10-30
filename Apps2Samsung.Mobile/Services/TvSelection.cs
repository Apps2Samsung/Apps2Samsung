using Apps2Samsung.Design;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Microsoft.Maui.ApplicationModel;

namespace Apps2Samsung.Mobile.Services;

/// <summary>One TV in the list: found by the scan, or typed in by hand.</summary>
/// <param name="Device">The scan result (Developer-Mode fields, DebugPortOpen); null for a typed IP
/// the TV's REST API didn't answer for.</param>
public sealed record TvEntry(string Ip, string Label, NetworkDevice? Device);

/// <summary>
/// The TVs the app knows about and the one it is working with, shared by every tab (#755): the ▾
/// in each tab's top bar opens the same TV sheet over this list, and Install, On the TV and Remote
/// follow <see cref="Changed"/>. Owns the scan and the hand-typed IPs. Process-lived, like
/// <see cref="SessionState"/>.
/// </summary>
public sealed class TvSelection
{
	private readonly INetworkService _network;
	private readonly List<TvEntry> _tvs = new();
	private int _selected = -1;

	public TvSelection(INetworkService network)
	{
		_network = network;
	}

	/// <summary>Raised on the main thread when the list, the selection, the scan state or the status
	/// line changes.</summary>
	public event Action? Changed;

	public IReadOnlyList<TvEntry> Tvs => _tvs;
	public int SelectedIndex => _selected;
	public TvEntry? Selected => _selected >= 0 && _selected < _tvs.Count ? _tvs[_selected] : null;

	public string? Ip => Selected?.Ip;
	public string Label => Selected?.Label ?? string.Empty;
	public NetworkDevice? Device => Selected?.Device;

	public bool HasTv => Selected is not null;
	/// <summary>False only for a TV the scan found without an open debug port.</summary>
	public bool DebugPortOpen => Device?.DebugPortOpen ?? true;

	public bool IsScanning { get; private set; }

	/// <summary>
	/// The last scan found nothing because the OS refused every probe (#824). On iOS that is the
	/// Local Network permission: denied, or still on screen while the first scan ran. The card then
	/// offers Settings instead of Add by IP, which the same block would stop.
	/// </summary>
	public bool LocalNetworkBlocked { get; private set; }
	private bool _retriedBlockedScan;

	/// <summary>Set by the Install tab while it installs: the TV can't change under a running install,
	/// so the TV sheet stays shut.</summary>
	public bool IsLocked { get; set; }

	/// <summary>The last thing the scan or the selection had to say ("No TVs found…", "Using the TV
	/// at…"); the Install tab shows it on the TV card.</summary>
	public string Status { get; private set; } = string.Empty;

	/// <summary>The TV's name for a title: its own name, else the generic "Samsung TV", else the IP.</summary>
	public string DisplayName => Selected is not { } tv
		? string.Empty
		: string.IsNullOrWhiteSpace(tv.Device?.DeviceName) ? tv.Ip : tv.Device!.DeviceName!;

	/// <summary>What the TV card and the top bars show for the selection (Core, shared with the
	/// desktop head): scanning, no TV, or the TV with its readiness.</summary>
	public TvCardState CardState()
	{
		if (IsScanning)
			return TvCardStates.Scanning(L10n.Get);
		if (Selected is not { } tv)
			return NoTvState();
		// The phone's IP as this TV sees it: on a hotspot TV that is the hotspot address, not the
		// cellular one the socket trick returns (#920).
		var localIp = NetworkInfo.LocalIpFor(tv.Ip);
		return TvCardStates.ForDevice(tv.Device ?? new NetworkDevice { IpAddress = tv.Ip }, localIp, L10n.Get);
	}

	// Nothing found. When the phone's only LAN is the hotspot it hosts, say so: the TV has to join
	// that hotspot, and the address shown is the one for its Host PC IP field.
	private TvCardState NoTvState()
	{
		if (LocalNetworks.HotspotOnly() is { } hotspot)
		{
			var ip = hotspot.Address.ToString();
			return TvCardStates.NoTv(L10n.Get, ip) with
			{
				Subline = string.Format(L10n.Get("tvCardNoTvHintHotspot"), ip),
			};
		}

		return TvCardStates.NoTv(L10n.Get, NetworkInfo.GetLocalIPv4());
	}

	/// <summary>Makes the TV at <paramref name="index"/> the one in use (−1 = none).</summary>
	public void Select(int index)
	{
		_selected = index >= 0 && index < _tvs.Count ? index : -1;
		Status = SelectedStatus();
		Raise();
	}

	public void Clear() => Select(-1);

	/// <summary>
	/// Scans the network. Not-ready TVs (REST API answered but the SDB debug port isn't up) are kept:
	/// they're listed amber so the user knows the TV was found but needs a restart first, instead of
	/// silently disappearing. The shared scan also enriches every TV with its Developer-Mode state,
	/// which the pre-install guards and the not-ready hint need. The selected TV stays selected when
	/// it is found again.
	/// </summary>
	public async Task ScanAsync()
	{
		if (IsScanning)
			return;
		IsScanning = true;
		Status = L10n.Get("statusScanningForTvs");
		Raise();
		try
		{
			var devices = await TizenDeveloperInfo.ScanAsync(_network);

			// iOS asks for Local Network access on the first LAN connect, which is this very scan:
			// every probe fails while the prompt is on screen. Once per app run, give the user a
			// moment to tap Allow and sweep again, so the first scan doesn't end in "no TV" with
			// the permission freshly granted (#824).
			if (devices.Count == 0 && _network.LastScanBlockedByOs && OperatingSystem.IsIOS() && !_retriedBlockedScan)
			{
				_retriedBlockedScan = true;
				await Task.Delay(TimeSpan.FromSeconds(3));
				devices = await TizenDeveloperInfo.ScanAsync(_network);
			}
			LocalNetworkBlocked = devices.Count == 0 && _network.LastScanBlockedByOs && OperatingSystem.IsIOS();
			TraceScanDiagnostics(devices);
			var keepIp = Ip;

			_tvs.Clear();
			foreach (var d in devices)
				_tvs.Add(new TvEntry(d.IpAddress, d.DisplayText, d));

			var keep = keepIp is null ? -1 : _tvs.FindIndex(t => t.Ip == keepIp);
			_selected = _tvs.Count > 0 ? (keep >= 0 ? keep : 0) : -1;
			Status = _tvs.Count == 0
				? L10n.Get(LocalNetworkBlocked ? "statusLocalNetworkBlocked" : "statusNoTvsFoundHint")
				: SelectedStatus();
		}
		catch (Exception ex)
		{
			LocalNetworkBlocked = false;
			Status = string.Format(L10n.Get("statusScanFailed"), ex.Message);
		}
		finally
		{
			IsScanning = false;
			Raise();
		}
	}

	/// <summary>
	/// Adds a TV the scan didn't find and selects it. The TV's REST API is asked for its
	/// Developer-Mode state so a hand-typed IP gets the same pre-install guards as a scanned one;
	/// nothing is lost when it doesn't answer: the entry is still added (assumed ready, a real SDB
	/// error surfaces at install) and stays unguarded. False for something that isn't an IP.
	/// </summary>
	public async Task<bool> AddManualAsync(string? input)
	{
		if (string.IsNullOrWhiteSpace(input) || !System.Net.IPAddress.TryParse(input.Trim(), out _))
		{
			if (!string.IsNullOrWhiteSpace(input))
			{
				Status = L10n.Get("statusInvalidIp");
				Raise();
			}
			return false;
		}

		var ip = input.Trim();
		var idx = _tvs.FindIndex(t => t.Ip == ip);
		if (idx < 0)
		{
			var probed = await ProbeTvAsync(ip);
			_tvs.Add(new TvEntry(ip, string.Format(L10n.Get("lblTvManualEntry"), ip), probed));
			idx = _tvs.Count - 1;
		}
		_selected = idx;
		Status = string.Format(L10n.Get("statusUsingTvAt"), ip);
		Raise();
		return true;
	}

	/// <summary>The device list is a scan snapshot; re-reads the selected TV's Developer-Mode fields
	/// so a Developer-Mode IP corrected on the TV since the scan isn't still flagged as a mismatch.</summary>
	public Task RefreshSelectedAsync() => TizenDeveloperInfo.RefreshAsync(_network, Device);

	/// <summary>
	/// Status line for the current selection. A TV that isn't installable yet gets the actual reason
	/// (Developer Mode off, its Developer-Mode IP pointing at another device, or a power cycle needed)
	/// instead of a bare count: the same guidance the desktop head shows.
	/// </summary>
	public string SelectedStatus()
	{
		if (Device is { DebugPortOpen: false } device)
		{
			var localIp = NetworkInfo.LocalIpFor(device.IpAddress);
			return TizenDeviceReadiness.Describe(TizenDeviceReadiness.WhyNotReady(device, localIp), localIp, L10n.Get);
		}

		var notReady = _tvs.Count(t => t.Device is { DebugPortOpen: false });
		return notReady > 0
			? string.Format(L10n.Get("statusTvsFoundNotReady"), _tvs.Count, notReady)
			: L10n.Get("Ready");
	}

	// One line per scan with everything the readiness verdict was computed from, the same line the
	// desktop writes (#922): what to look at in the debug log when a TV is shown as not ready.
	private void TraceScanDiagnostics(IReadOnlyList<NetworkDevice> devices)
	{
		try
		{
			var localIps = _network.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList();
			System.Diagnostics.Trace.WriteLine(ScanDiagnostics.Describe(
				NetworkInfo.GetLocalIPv4(), _network.GetLocalIPAddress(), localIps, devices));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[Scan] diagnostics failed: {ex.Message}");
		}
	}

	// Reads a TV's Developer-Mode state over its REST API; null when it doesn't answer.
	private async Task<NetworkDevice?> ProbeTvAsync(string ip)
	{
		try
		{
			var enriched = await TizenDeveloperInfo.EnrichAsync(
				_network, new[] { new NetworkDevice { IpAddress = ip } });
			return enriched.FirstOrDefault();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tv] Couldn't read TV info for {ip}: {ex.Message}");
			return null;
		}
	}

	private void Raise() => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
}
