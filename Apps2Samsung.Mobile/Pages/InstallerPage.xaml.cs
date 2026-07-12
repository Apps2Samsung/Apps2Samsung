using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Update;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallerPage : ContentPage
{
	// Synthetic App-picker entry for installing a local .wgt (mirrors the desktop's option).
	private const string CustomWgtLabel = "📁 Custom WGT file…";

	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly CatalogService _catalog;
	private readonly GitHubUpdateChecker _updateChecker;
	private readonly SessionState _session;

	// Parallel to TvPicker items: the IP for each listed (debug-ready) TV.
	private readonly List<string> _tvIps = new();
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
		GitHubUpdateChecker updateChecker, SessionState session)
	{
		InitializeComponent();
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_catalog = catalog;
		_updateChecker = updateChecker;
		_session = session;

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
			var labels = new List<string>();
			foreach (var d in devices)
			{
				_tvIps.Add(d.IpAddress);
				labels.Add(d.DisplayText);
			}

			TvPicker.ItemsSource = labels;
			if (_tvIps.Count > 0)
			{
				var keep = selected is null ? 0 : _tvIps.IndexOf(selected);
				TvPicker.SelectedIndex = keep >= 0 ? keep : 0;
			}

			SetStatus(_tvIps.Count == 0
				? "No debug-ready TVs found. Enable Developer Mode on the TV, then refresh."
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

		InstallBtn.IsEnabled = false;
		RefreshBtn.IsEnabled = false;
		string? wgtPath = null;
		try
		{
			if (!_session.IsSignedIn)
			{
				SetStatus("Signing in to Samsung…");
				_session.Auth = await _loginService.LoginAsync();
			}

			var cert = await _certProvisioner.ProvisionAsync(tvIp, _session.Auth!, SetStatus);
			wgtPath = custom ? _customWgtPath! : await _installer.DownloadAsync(asset!.DownloadUrl, SetStatus);
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

	private void SetStatus(string message) =>
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
}
