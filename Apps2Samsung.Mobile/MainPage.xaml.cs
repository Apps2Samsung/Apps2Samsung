using System.Linq;
using System.Text;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile;

public partial class MainPage : ContentPage
{
	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;

	// Held between the smoke steps: sign in, scan (pick first debug-ready TV), then provision.
	private SamsungAuth? _auth;
	private string? _readyTvIp;

	public MainPage(INetworkService networkService, ISamsungLoginService loginService, CertificateProvisioner certProvisioner)
	{
		InitializeComponent();
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
	}

	private async void OnScanClicked(object? sender, EventArgs e)
	{
		ScanBtn.IsEnabled = false;
		Busy.IsRunning = Busy.IsVisible = true;
		ResultsLabel.Text = "Scanning…";

		try
		{
			var devices = (await _networkService.GetLocalTizenAddresses()).ToList();

			if (devices.Count == 0)
			{
				ResultsLabel.Text = "No TVs found. Make sure the TV is on the same Wi-Fi and Developer Mode is enabled.";
				return;
			}

			_readyTvIp = devices.FirstOrDefault(d => d.DebugPortOpen)?.IpAddress;

			var sb = new StringBuilder($"Found {devices.Count} device(s):\n");
			foreach (var d in devices)
			{
				var name = string.IsNullOrWhiteSpace(d.DeviceName) ? "(unnamed)" : d.DeviceName;
				var state = d.DebugPortOpen ? "debug ready" : "not ready (enable Developer Mode)";
				sb.AppendLine($"• {d.IpAddress} — {name} [{state}]");
			}
			ResultsLabel.Text = sb.ToString().TrimEnd();
		}
		catch (Exception ex)
		{
			ResultsLabel.Text = $"Scan failed: {ex.Message}";
		}
		finally
		{
			Busy.IsRunning = Busy.IsVisible = false;
			ScanBtn.IsEnabled = true;
		}
	}

	private async void OnLoginClicked(object? sender, EventArgs e)
	{
		LoginBtn.IsEnabled = false;
		ResultsLabel.Text = "Opening Samsung sign-in…";

		try
		{
			_auth = await _loginService.LoginAsync();
			var email = string.IsNullOrWhiteSpace(_auth.inputEmailID) ? "(no email in token)" : _auth.inputEmailID;
			var haveToken = !string.IsNullOrWhiteSpace(_auth.access_token);
			ResultsLabel.Text = $"Signed in as {email}\nUser id: {_auth.userId}\nAccess token: {(haveToken ? "received ✓" : "missing ✗")}";
		}
		catch (TaskCanceledException)
		{
			ResultsLabel.Text = "Sign-in cancelled.";
		}
		catch (Exception ex)
		{
			ResultsLabel.Text = $"Sign-in failed: {ex.Message}";
		}
		finally
		{
			LoginBtn.IsEnabled = true;
		}
	}

	private async void OnProvisionClicked(object? sender, EventArgs e)
	{
		if (_auth is null)
		{
			ResultsLabel.Text = "Sign in to Samsung first.";
			return;
		}
		if (string.IsNullOrEmpty(_readyTvIp))
		{
			ResultsLabel.Text = "Scan first and make sure a debug-ready TV was found.";
			return;
		}

		ProvisionBtn.IsEnabled = false;
		Busy.IsRunning = Busy.IsVisible = true;

		try
		{
			var result = await _certProvisioner.ProvisionAsync(
				_readyTvIp,
				_auth,
				step => MainThread.BeginInvokeOnMainThread(() => ResultsLabel.Text = $"Provisioning: {step}"));

			ResultsLabel.Text =
				$"Certificate ready for {_readyTvIp}\nDUID: {result.Duid}\n" +
				$"Author: {Path.GetFileName(result.AuthorP12)}\nDistributor: {Path.GetFileName(result.DistributorP12)}\n" +
				$"Saved to: {result.ProfileDir}";
		}
		catch (Exception ex)
		{
			ResultsLabel.Text = $"Provisioning failed: {ex.Message}";
		}
		finally
		{
			Busy.IsRunning = Busy.IsVisible = false;
			ProvisionBtn.IsEnabled = true;
		}
	}
}
