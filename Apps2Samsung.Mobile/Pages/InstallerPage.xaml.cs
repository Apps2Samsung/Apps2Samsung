using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallerPage : ContentPage
{
	// App catalog (hardcoded for now; a real catalog/picker is a later step).
	private static readonly (string Name, string Url)[] Apps =
	{
		("Jellyfin", "https://github.com/jeppevinkel/jellyfin-tizen-builds/releases/latest/download/Jellyfin.wgt"),
	};

	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly SessionState _session;

	// Parallel to TvPicker items: the IP for each listed (debug-ready) TV.
	private readonly List<string> _tvIps = new();
	private bool _scannedOnce;

	public InstallerPage(INetworkService networkService, ISamsungLoginService loginService,
		CertificateProvisioner certProvisioner, WgtInstaller installer, SessionState session)
	{
		InitializeComponent();
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_session = session;

		AppPicker.ItemsSource = Apps.Select(a => a.Name).ToList();
		AppPicker.SelectedIndex = 0;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (!_scannedOnce)
			await ScanAsync();
	}

	private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

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
				var name = string.IsNullOrWhiteSpace(d.DeviceName) ? "Samsung TV" : d.DeviceName!;
				labels.Add($"{name} — {d.IpAddress}");
			}

			TvPicker.ItemsSource = labels;
			if (_tvIps.Count > 0)
			{
				var keep = selected is null ? 0 : _tvIps.IndexOf(selected);
				TvPicker.SelectedIndex = keep >= 0 ? keep : 0;
			}

			SetStatus(_tvIps.Count == 0
				? "No debug-ready TVs found. Enable Developer Mode on the TV, then refresh."
				: $"Found {_tvIps.Count} TV(s). Pick one and install.");
			_scannedOnce = true;
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
		if (AppPicker.SelectedIndex < 0)
		{
			SetStatus("Select an app first.");
			return;
		}

		var tvIp = _tvIps[TvPicker.SelectedIndex];
		var url = Apps[AppPicker.SelectedIndex].Url;

		InstallBtn.IsEnabled = false;
		RefreshBtn.IsEnabled = false;
		try
		{
			if (!_session.IsSignedIn)
			{
				SetStatus("Signing in to Samsung…");
				_session.Auth = await _loginService.LoginAsync();
			}

			var cert = await _certProvisioner.ProvisionAsync(tvIp, _session.Auth!, SetStatus);
			var wgtPath = await _installer.DownloadAsync(url, SetStatus);
			await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus);

			SetStatus($"✓ Installed {Apps[AppPicker.SelectedIndex].Name}. Open the TV's Apps list to launch it.");
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
			InstallBtn.IsEnabled = true;
			RefreshBtn.IsEnabled = true;
		}
	}

	private void SetStatus(string message) =>
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
}
