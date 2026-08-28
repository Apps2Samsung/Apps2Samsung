using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Packaging;
using Apps2Samsung.Update;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallerPage : ContentPage
{
	// Synthetic App-picker entry for installing a local .wgt (mirrors the desktop's option).
	private const string CustomWgtLabel = "📁 Custom WGT file…";
	// Synthetic TV-picker entry for targeting a TV the scan didn't find (mirrors the desktop's
	// manual-IP option). Always the last item in the TV list.
	private const string ManualIpLabel = "✏️ Enter IP manually…";

	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly CatalogService _catalog;
	private readonly GitHubUpdateChecker _updateChecker;
	private readonly SessionState _session;
	private readonly ISdbEngine _sdb;

	// Parallel lists backing the TV picker: IP + display label for each listed TV (discovered or
	// manually added). The picker also shows a trailing ManualIpLabel entry that isn't in these.
	private readonly List<string> _tvIps = new();
	private readonly List<string> _tvLabels = new();
	// Per-TV scan result, index-aligned with _tvIps (null for a hand-typed IP the TV's REST API didn't
	// answer for). Carries DebugPortOpen — false = detected via the REST API only (SDB debug port not
	// up yet), so it needs a restart before it can be installed to — plus the Developer-Mode fields
	// the shared pre-install guards check.
	private readonly List<NetworkDevice?> _tvDevices = new();
	// Suppresses OnTvChanged while we rebuild the picker programmatically.
	private bool _rebuildingTvPicker;
	// The catalog releases backing AppPicker; the selected release's assets back VersionPicker.
	private IReadOnlyList<GitHubRelease> _releases = new List<GitHubRelease>();
	private List<Asset> _versions = new();
	// A cache copy of a user-picked .wgt (custom install); null until picked.
	private string? _customWgtPath;

	private bool _initialized;
	private bool _uiReady;

	private bool CustomSelected => (AppPicker.SelectedItem as string) == CustomWgtLabel;

	public InstallerPage(INetworkService networkService, ISamsungLoginService loginService,
		CertificateProvisioner certProvisioner, WgtInstaller installer, CatalogService catalog,
		GitHubUpdateChecker updateChecker, SessionState session, ISdbEngine sdb)
	{
		InitializeComponent();
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_catalog = catalog;
		_updateChecker = updateChecker;
		_session = session;
		_sdb = sdb;

		// Shows the app version (ApplicationDisplayVersion), so it stays in sync with the build.
		VersionLabel.Text = $"v{AppInfo.Current.VersionString}";

		var ip = NetworkInfo.GetLocalIPv4();
		PhoneIpLabel.Text = ip is null ? "📱 This phone: offline" : $"📱 This phone: {ip}";
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_initialized)
			return;
		_initialized = true;

		await MobileSettings.InitAsync();
		var catalog = await LoadCatalogAsync();
		await ScanAsync();

		// A catalog problem is more actionable than the scan result, so let it have the last word.
		// (The "Custom WGT file" entry is always available, so an empty catalog isn't a dead end.)
		if (catalog is null)
			SetStatus(L10n.Get("statusCatalogUnavailable"));
		else if (catalog.Releases.Count == 0)
			SetStatus(L10n.Get("statusCatalogEmpty"));
		else if (catalog.Failed > 0)
			SetStatus(string.Format(L10n.Get("statusCatalogPartial"), catalog.Failed, catalog.Total));

		// Quietly check for a newer build (best-effort; never blocks the UI).
		_ = CheckForUpdatesAsync();
	}

	private async Task CheckForUpdatesAsync()
	{
		try
		{
			var current = AppInfo.Current.VersionString;
			var result = await _updateChecker.CheckForUpdateAsync(
				current,
				includePrereleases: MobileSettings.IncludeBetaUpdates, // beta channel toggle (Settings)
				assetMatcher: name => name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

			if (!result.IsSuccess || !result.IsUpdateAvailable)
				return;

			var download = await DisplayAlert(
				L10n.Get("UpdateAvailable"),
				string.Format(L10n.Get("statusUpdateAvailableApk"), result.LatestVersion, current),
				L10n.Get("lblDownload"), L10n.Get("lblLater"));

			if (download)
				await Launcher.Default.OpenAsync(result.DownloadUrl ?? result.ReleasesPageUrl);
		}
		catch
		{
			// Update check is best-effort — offline / rate-limited is fine.
		}
	}

	private async Task<CatalogService.CatalogResult?> LoadCatalogAsync()
	{
		SetStatus(L10n.Get("lblLoadingAppList"));
		CatalogService.CatalogResult? result = null;
		try
		{
			result = await _catalog.LoadReleasesAsync();
			_releases = result.Releases;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusCatalogFailed"), ex.Message));
			_releases = new List<GitHubRelease>();
		}

		// Real apps first, then the always-present "Custom WGT file" entry.
		var items = _releases.Select(r => r.Name).ToList();
		items.Add(CustomWgtLabel);
		AppPicker.ItemsSource = items;
		AppPicker.SelectedIndex = _releases.Count > 0 ? 0 : items.Count - 1;

		_uiReady = true;
		return result;
	}

	private async void OnAppChanged(object? sender, EventArgs e)
	{
		if (CustomSelected)
		{
			_versions = new();
			VersionPicker.ItemsSource = null;
			_customWgtPath = null;
			// Prompt for a file when the user picks this (not during the initial default selection).
			if (_uiReady)
				await PickCustomWgtAsync();
			return;
		}

		var i = AppPicker.SelectedIndex;
		if (i < 0 || i >= _releases.Count)
		{
			_versions = new();
			VersionPicker.ItemsSource = null;
			return;
		}

		_versions = _releases[i].Assets;
		VersionPicker.ItemsSource = _versions.Select(a => a.DisplayText).ToList();
		if (_versions.Count > 0)
		{
			var def = _versions.FindIndex(a => a.IsDefault);
			VersionPicker.SelectedIndex = def >= 0 ? def : 0;
		}
	}

	// Lets the user pick a local .wgt; copies it into the cache so re-signing/cleanup never touches
	// their original file. Returns true if a valid file was selected.
	private async Task<bool> PickCustomWgtAsync()
	{
		try
		{
			var picked = await SafFilePicker.PickAsync();
			if (picked is null)
			{
				SetStatus(L10n.Get("statusNoFileSelected"));
				return false;
			}
			if (!picked.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus(L10n.Get("statusChooseWgt"));
				return false;
			}

			var dest = Path.Combine(FileSystem.CacheDirectory, picked.FileName);
			File.Copy(picked.LocalPath, dest, overwrite: true);

			_customWgtPath = dest;
			VersionPicker.ItemsSource = new List<string> { picked.FileName };
			VersionPicker.SelectedIndex = 0;
			SetStatus(string.Format(L10n.Get("statusCustomPackageReady"), picked.FileName));
			return true;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusFileUnreadable"), ex.Message));
			return false;
		}
	}

	// Rebuilds the TV picker from _tvLabels plus the trailing "enter IP manually" entry, selecting the
	// given index (−1 = leave unselected). Guarded so it doesn't re-enter OnTvChanged.
	private void RebuildTvPicker(int selectIndex)
	{
		_rebuildingTvPicker = true;
		try
		{
			var items = new List<string>(_tvLabels) { ManualIpLabel };
			TvPicker.ItemsSource = items;
			TvPicker.SelectedIndex = selectIndex >= 0 && selectIndex < items.Count ? selectIndex : -1;
		}
		finally { _rebuildingTvPicker = false; }
	}

	private async void OnTvChanged(object? sender, EventArgs e)
	{
		if (_rebuildingTvPicker)
			return;
		if ((TvPicker.SelectedItem as string) == ManualIpLabel)
		{
			await PromptManualIpAsync();
			return;
		}

		SetStatus(SelectedTvStatus());
	}

	// Prompts for a TV IP the scan didn't discover, validates it, and adds it as a selectable entry.
	private async Task PromptManualIpAsync()
	{
		var input = await DisplayPromptAsync(
			"Manual TV IP",
			"Enter the TV's IP address (Developer Mode must be on):",
			accept: "Add", cancel: "Cancel", placeholder: "192.168.1.50");

		if (string.IsNullOrWhiteSpace(input) || !System.Net.IPAddress.TryParse(input.Trim(), out _))
		{
			if (!string.IsNullOrWhiteSpace(input))
				SetStatus(L10n.Get("statusInvalidIp"));
			// Move the selection off the manual entry (back to the first real TV, if any).
			RebuildTvPicker(_tvIps.Count > 0 ? 0 : -1);
			return;
		}

		var ip = input.Trim();
		var idx = _tvIps.IndexOf(ip);
		if (idx < 0)
		{
			// Ask the TV's REST API for its Developer-Mode state so a hand-typed IP gets the same
			// pre-install guards as a scanned one. Nothing is lost when it doesn't answer: the entry
			// is still added (assumed ready, a real SDB error surfaces at install) and stays unguarded.
			var probed = await ProbeTvAsync(ip);
			_tvIps.Add(ip);
			_tvLabels.Add($"{ip} (manual)");
			_tvDevices.Add(probed);
			idx = _tvIps.Count - 1;
		}
		RebuildTvPicker(idx);
		SetStatus(string.Format(L10n.Get("statusUsingTvAt"), ip));
	}

	private async void OnShowInstalledAppsClicked(object? sender, EventArgs e)
	{
		if (TvPicker.SelectedIndex < 0 || TvPicker.SelectedIndex >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var tvIp = _tvIps[TvPicker.SelectedIndex];
		var label = TvPicker.SelectedItem as string ?? tvIp;
		await Navigation.PushAsync(new InstalledAppsPage(_sdb, tvIp, label));
	}

	private async void OnShowDeviceInfoClicked(object? sender, EventArgs e)
	{
		if (TvPicker.SelectedIndex < 0 || TvPicker.SelectedIndex >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var idx = TvPicker.SelectedIndex;
		var tvIp = _tvIps[idx];
		var label = TvPicker.SelectedItem as string ?? tvIp;
		var debugPortOpen = idx >= _tvDevices.Count || (_tvDevices[idx]?.DebugPortOpen ?? true);
		await Navigation.PushAsync(new DeviceInfoPage(_sdb, tvIp, label, debugPortOpen));
	}

	// The remote needs only a TV on the network — no Developer Mode, no debug port — so it is
	// offered for any selected TV, including one the installer itself couldn't use.
	private async void OnShowRemoteClicked(object? sender, EventArgs e)
	{
		if (TvPicker.SelectedIndex < 0 || TvPicker.SelectedIndex >= _tvIps.Count)
		{
			SetStatus("Select a TV first (tap refresh to scan).");
			return;
		}
		var tvIp = _tvIps[TvPicker.SelectedIndex];
		var label = TvPicker.SelectedItem as string ?? tvIp;
		await Navigation.PushAsync(new RemotePage(tvIp, label));
	}

	private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

	private async void OnSettingsClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(new SettingsPage());

	private async void OnCatalogClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(new BuildInfoPage(_catalog));

	private async Task ScanAsync()
	{
		RefreshBtn.IsEnabled = false;
		SetStatus(L10n.Get("statusScanningForTvs"));
		try
		{
			// Keep not-ready TVs (REST API answered but the SDB debug port isn't up): they're shown
			// with a ⚠ marker so the user knows the TV was found but needs a restart first, instead of
			// silently disappearing from the list. The shared scan also enriches every TV with its
			// Developer-Mode state (name, developerMode, developerIP) — what the guards below and the
			// not-ready hint need to tell the user what to fix.
			var devices = await TizenDeveloperInfo.ScanAsync(_networkService);

			var selected = TvPicker.SelectedIndex >= 0 && TvPicker.SelectedIndex < _tvIps.Count
				? _tvIps[TvPicker.SelectedIndex]
				: null;

			_tvIps.Clear();
			_tvLabels.Clear();
			_tvDevices.Clear();
			foreach (var d in devices)
			{
				_tvIps.Add(d.IpAddress);
				_tvLabels.Add(d.DisplayText);
				_tvDevices.Add(d);
			}

			var keep = selected is null ? -1 : _tvIps.IndexOf(selected);
			RebuildTvPicker(_tvIps.Count > 0 ? (keep >= 0 ? keep : 0) : -1);

			SetStatus(_tvIps.Count == 0
				? L10n.Get("statusNoTvsFoundHint")
				: SelectedTvStatus());
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusScanFailed"), ex.Message));
		}
		finally
		{
			RefreshBtn.IsEnabled = true;
		}
	}

	private async void OnInstallClicked(object? sender, EventArgs e)
	{
		if (TvPicker.SelectedIndex < 0 || TvPicker.SelectedIndex >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}

		var custom = CustomSelected;
		if (custom)
		{
			// If they chose the custom entry but haven't picked a file yet, prompt now.
			if (_customWgtPath is null && !await PickCustomWgtAsync())
				return;
		}
		else if (VersionPicker.SelectedIndex < 0 || VersionPicker.SelectedIndex >= _versions.Count)
		{
			SetStatus(L10n.Get("statusSelectAppFirst"));
			return;
		}

		var tvIp = _tvIps[TvPicker.SelectedIndex];

		// Shared pre-install guards (Core): a TV that still needs a restart, Developer Mode off, a
		// Developer-Mode IP pointing at another device or typed back to front, a TV on another subnet.
		// Same checks and wording as the desktop head — each one is Continue/Stop, never a hard block.
		var guardResult = InstallGuards.Evaluate(
			_tvDevices[TvPicker.SelectedIndex],
			new InstallGuardOptions
			{
				LocalIps = LocalIps(),
				ConfiguredLocalIp = NetworkInfo.GetLocalIPv4(),
			},
			_networkService);

		foreach (var guard in guardResult.Guards)
		{
			bool continueAnyway = await DisplayAlert(
				guard.DefaultTitle, guard.DefaultMessageWithDetail,
				L10n.Get("keyContinue"), L10n.Get("keyStop"));
			if (!continueAnyway)
				return;
		}

		tvIp = guardResult.CorrectedTvIp ?? tvIp;

		var asset = custom ? null : _versions[VersionPicker.SelectedIndex];
		var appName = custom
			? Path.GetFileNameWithoutExtension(_customWgtPath!)
			: (AppPicker.SelectedItem as string ?? "app");
		// Auto-request Partner signing if the selected app's manifest declares cert_level: partner.
		var requirePartner = !custom
			&& AppPicker.SelectedIndex >= 0 && AppPicker.SelectedIndex < _releases.Count
			&& _releases[AppPicker.SelectedIndex].RequiresPartner;

		InstallBtn.IsEnabled = false;
		RefreshBtn.IsEnabled = false;
		string? wgtPath = null;
		try
		{
			// Download first so we can inspect the package's declared privileges before signing.
			wgtPath = custom ? _customWgtPath! : await _installer.DownloadAsync(asset!.DownloadUrl, SetStatus);

			// Partner if the manifest declared it OR the .wgt itself needs a partner-level privilege.
			var needsPartner = requirePartner || WgtPrivileges.RequiresPartner(wgtPath);
			// Provisioning reuses a valid cert already covering this TV and only triggers a Samsung
			// sign-in when it must (re)generate — so no login prompt when a cert is already in place.
			var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);

			// The signing certificate's validity period may not have started yet — a Samsung TV
			// rejects such a package ("Certificate in signature is not valid yet"). Hold the install
			// behind a countdown to the moment it becomes valid instead of pushing something the TV
			// will refuse; the loop re-runs the check once the user chooses to continue.
			while (true)
			{
				try
				{
					await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus);
					break;
				}
				catch (CertificateNotYetValidException notYetValid)
				{
					SetStatus(L10n.Get("certificateWaiting"));
					var wait = new CertificateWaitPage(notYetValid.Result);
					await Navigation.PushModalAsync(wait);
					if (!await wait.Completion)
					{
						SetStatus(L10n.Get("certificateWaitCancelled"));
						return;
					}
				}
			}

			SetStatus("✓ " + string.Format(L10n.Get("statusInstalledOpenApps"), appName));
			await Navigation.PushModalAsync(new InstallCompletePage(appName));
		}
		catch (TaskCanceledException)
		{
			SetStatus(L10n.Get("statusSignInCancelled"));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Install failed: {ex}");
			SetStatus(string.Format(L10n.Get("statusInstallFailed"), ex.Message));
		}
		finally
		{
			// Clean up the staged package unless the user opted to keep it. For a custom install
			// this is the cache copy, never the user's original file.
			if (wgtPath is not null && !MobileSettings.KeepWgtFile)
			{
				try { File.Delete(wgtPath); } catch { /* best-effort */ }
				if (custom)
					_customWgtPath = null; // force a re-pick next time (the copy is gone)
			}

			InstallBtn.IsEnabled = true;
			RefreshBtn.IsEnabled = true;
		}
	}

	// The IPs this phone answers on — what the guards compare the TV's Developer-Mode IP against.
	// On Android interface enumeration classifies nothing, so this resolves to the socket-derived
	// phone IP the scanner is configured with (empty when offline: the guards then skip the IP
	// checks rather than guess).
	private List<string> LocalIps()
	{
		try
		{
			return _networkService.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Couldn't enumerate local IPs: {ex.Message}");
			return new List<string>();
		}
	}

	// Reads a TV's Developer-Mode state over its REST API; null when it doesn't answer.
	private async Task<NetworkDevice?> ProbeTvAsync(string ip)
	{
		try
		{
			var enriched = await TizenDeveloperInfo.EnrichAsync(
				_networkService, new[] { new NetworkDevice { IpAddress = ip } });
			return enriched.FirstOrDefault();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Couldn't read TV info for {ip}: {ex.Message}");
			return null;
		}
	}

	// Status line for the current selection. A TV that isn't installable yet gets the actual reason
	// (Developer Mode off, its Developer-Mode IP pointing at another device, or a power cycle needed)
	// instead of a bare count — the same guidance the desktop head shows.
	private string SelectedTvStatus()
	{
		var idx = TvPicker.SelectedIndex;
		var device = idx >= 0 && idx < _tvDevices.Count ? _tvDevices[idx] : null;
		var localIp = NetworkInfo.GetLocalIPv4();

		if (device is { DebugPortOpen: false })
			return TizenDeviceReadiness.Describe(TizenDeviceReadiness.WhyNotReady(device, localIp), localIp);

		var notReady = _tvDevices.Count(d => d is { DebugPortOpen: false });
		return notReady > 0
			? $"Found {_tvIps.Count} TV(s). ⚠ {notReady} not ready — select one to see why."
			: "Ready for use…";
	}

	private void SetStatus(string message)
	{
		// Mirror every status line to the debug log (timestamped) so the log is a full transcript of
		// the scan/download/cert/install flow — not just the on-screen label.
		System.Diagnostics.Trace.WriteLine($"[installer] {message}");
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
	}
}
