using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Mobile.Catalog;
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
			SetStatus("Couldn't load the app list — you can still install a custom .wgt.");
		else if (catalog.Releases.Count == 0)
			SetStatus("No apps loaded (offline or GitHub rate-limited). Add a token in Settings, or install a custom .wgt.");
		else if (catalog.Failed > 0)
			SetStatus($"Ready — but {catalog.Failed} of {catalog.Total} app sources failed (likely GitHub rate limit). Add a GitHub token in Settings.");

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
				includePrereleases: true, // mobile ships as -beta releases
				assetMatcher: name => name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

			if (!result.IsSuccess || !result.IsUpdateAvailable)
				return;

			var download = await DisplayAlert(
				"Update available",
				$"{result.LatestVersion} is available (you have v{current}). Download the new APK?",
				"Download", "Later");

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
		SetStatus("Loading apps…");
		CatalogService.CatalogResult? result = null;
		try
		{
			result = await _catalog.LoadReleasesAsync();
			_releases = result.Releases;
		}
		catch (Exception ex)
		{
			SetStatus($"Couldn't load the app list: {ex.Message}");
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
			var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select a .wgt package" });
			if (result is null)
			{
				SetStatus("No file selected.");
				return false;
			}
			if (!result.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus("Please choose a .wgt file.");
				return false;
			}

			var dest = Path.Combine(FileSystem.CacheDirectory, result.FileName);
			using (var src = await result.OpenReadAsync())
			using (var dst = File.Create(dest))
				await src.CopyToAsync(dst);

			_customWgtPath = dest;
			VersionPicker.ItemsSource = new List<string> { result.FileName };
			VersionPicker.SelectedIndex = 0;
			SetStatus($"Custom package ready: {result.FileName}");
			return true;
		}
		catch (Exception ex)
		{
			SetStatus($"Couldn't read the file: {ex.Message}");
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
			await PromptManualIpAsync();
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
				SetStatus("That doesn't look like a valid IP address.");
			// Move the selection off the manual entry (back to the first real TV, if any).
			RebuildTvPicker(_tvIps.Count > 0 ? 0 : -1);
			return;
		}

		var ip = input.Trim();
		var idx = _tvIps.IndexOf(ip);
		if (idx < 0)
		{
			_tvIps.Add(ip);
			_tvLabels.Add($"{ip} (manual)");
			idx = _tvIps.Count - 1;
		}
		RebuildTvPicker(idx);
		SetStatus($"Using TV at {ip}.");
	}

	private async void OnShowInstalledAppsClicked(object? sender, EventArgs e)
	{
		if (TvPicker.SelectedIndex < 0 || TvPicker.SelectedIndex >= _tvIps.Count)
		{
			SetStatus("Select a TV first (tap refresh to scan).");
			return;
		}
		var tvIp = _tvIps[TvPicker.SelectedIndex];
		var label = TvPicker.SelectedItem as string ?? tvIp;
		await Navigation.PushAsync(new InstalledAppsPage(_sdb, tvIp, label));
	}

	private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

	private async void OnSettingsClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(new SettingsPage());

	private async void OnCatalogClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(new BuildInfoPage(_catalog));

	private async Task ScanAsync()
	{
		RefreshBtn.IsEnabled = false;
		SetStatus("Scanning for TVs…");
		try
		{
			var devices = (await _networkService.GetLocalTizenAddresses())
				.Where(d => d.DebugPortOpen)
				.ToList();

			var selected = TvPicker.SelectedIndex >= 0 && TvPicker.SelectedIndex < _tvIps.Count
				? _tvIps[TvPicker.SelectedIndex]
				: null;

			_tvIps.Clear();
			_tvLabels.Clear();
			foreach (var d in devices)
			{
				_tvIps.Add(d.IpAddress);
				_tvLabels.Add(d.DisplayText);
			}

			var keep = selected is null ? -1 : _tvIps.IndexOf(selected);
			RebuildTvPicker(_tvIps.Count > 0 ? (keep >= 0 ? keep : 0) : -1);

			SetStatus(_tvIps.Count == 0
				? "No debug-ready TVs found. Enable Developer Mode on the TV and refresh, or tap the TV list to enter an IP manually."
				: "Ready for use…");
		}
		catch (Exception ex)
		{
			SetStatus($"Scan failed: {ex.Message}");
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
			SetStatus("Select a TV first (tap refresh to scan).");
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
			SetStatus("Select an app and version first.");
			return;
		}

		var tvIp = _tvIps[TvPicker.SelectedIndex];
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
			await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus);

			SetStatus($"✓ Installed {appName}. Open the TV's Apps list to launch it.");
			await Navigation.PushModalAsync(new InstallCompletePage(appName));
		}
		catch (TaskCanceledException)
		{
			SetStatus("Sign-in cancelled.");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Install failed: {ex}");
			SetStatus($"Install failed: {ex.Message}");
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

	private void SetStatus(string message)
	{
		// Mirror every status line to the debug log (timestamped) so the log is a full transcript of
		// the scan/download/cert/install flow — not just the on-screen label.
		System.Diagnostics.Trace.WriteLine($"[installer] {message}");
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
	}
}
