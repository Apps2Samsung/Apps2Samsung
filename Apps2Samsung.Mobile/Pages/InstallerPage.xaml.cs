using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallerPage : ContentPage
{
	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly CatalogService _catalog;
	private readonly SessionState _session;

	// Parallel to TvPicker items: the IP for each listed (debug-ready) TV.
	private readonly List<string> _tvIps = new();
	// The catalog releases backing AppPicker; the selected release's assets back VersionPicker.
	private IReadOnlyList<GitHubRelease> _releases = new List<GitHubRelease>();
	private List<Asset> _versions = new();

	private bool _initialized;

	public InstallerPage(INetworkService networkService, ISamsungLoginService loginService,
		CertificateProvisioner certProvisioner, WgtInstaller installer, CatalogService catalog,
		SessionState session)
	{
		InitializeComponent();
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_catalog = catalog;
		_session = session;

		// Shows the app version (ApplicationDisplayVersion), so it stays in sync with the build.
		VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
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
		if (catalog is null)
			SetStatus("Couldn't load the app list. Check your connection and reopen.");
		else if (catalog.Releases.Count == 0)
			SetStatus("No apps loaded — you're offline or GitHub rate-limited. Add a GitHub token in Settings.");
		else if (catalog.Failed > 0)
			SetStatus($"Ready — but {catalog.Failed} of {catalog.Total} app sources failed (likely GitHub rate limit). Add a GitHub token in Settings.");
	}

	private async Task<CatalogService.CatalogResult?> LoadCatalogAsync()
	{
		SetStatus("Loading apps…");
		try
		{
			var result = await _catalog.LoadReleasesAsync();
			_releases = result.Releases;
			AppPicker.ItemsSource = _releases.Select(r => r.Name).ToList();
			if (_releases.Count > 0)
				AppPicker.SelectedIndex = 0; // triggers OnAppChanged → populates versions
			return result;
		}
		catch (Exception ex)
		{
			SetStatus($"Couldn't load the app list: {ex.Message}");
			return null;
		}
	}

	private void OnAppChanged(object? sender, EventArgs e)
	{
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

	private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

	private async void OnSettingsClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(new SettingsPage());

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
		if (VersionPicker.SelectedIndex < 0 || VersionPicker.SelectedIndex >= _versions.Count)
		{
			SetStatus("Select an app and version first.");
			return;
		}

		var tvIp = _tvIps[TvPicker.SelectedIndex];
		var appName = AppPicker.SelectedItem as string ?? "app";
		var asset = _versions[VersionPicker.SelectedIndex];

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
			wgtPath = await _installer.DownloadAsync(asset.DownloadUrl, SetStatus);
			await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus);

			SetStatus($"✓ Installed {appName}. Open the TV's Apps list to launch it.");
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
			// Clean up the downloaded package unless the user opted to keep it.
			if (wgtPath is not null && !MobileSettings.KeepWgtFile)
				try { File.Delete(wgtPath); } catch { /* best-effort */ }

			InstallBtn.IsEnabled = true;
			RefreshBtn.IsEnabled = true;
		}
	}

	private void SetStatus(string message) =>
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
}
