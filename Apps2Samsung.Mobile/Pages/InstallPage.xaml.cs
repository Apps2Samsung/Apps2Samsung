using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Graphics;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallPage : ContentPage
{
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly SessionState _session;

	private string _tvIp = string.Empty;
	private string _tvName = string.Empty;

	public InstallPage(ISamsungLoginService loginService, CertificateProvisioner certProvisioner, WgtInstaller installer, SessionState session)
	{
		InitializeComponent();
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_session = session;
	}

	public void SetTarget(string ip, string name)
	{
		_tvIp = ip;
		_tvName = name;
		TargetNameLabel.Text = name;
		TargetIpLabel.Text = ip;
	}

	private async void OnInstallClicked(object? sender, EventArgs e)
	{
		var url = WgtUrlEntry.Text?.Trim();
		if (string.IsNullOrEmpty(url))
		{
			await DisplayAlert("Missing URL", "Enter a .wgt package URL.", "OK");
			return;
		}

		InstallBtn.IsEnabled = false;
		ResultCard.IsVisible = false;
		ProgressRow.IsVisible = true;
		Busy.IsRunning = true;

		try
		{
			// 1) Samsung sign-in (once per app run).
			if (!_session.IsSignedIn)
			{
				SetStatus("Signing in to Samsung…");
				_session.Auth = await _loginService.LoginAsync();
			}

			// 2) Provision the author/distributor certificates for this TV.
			var cert = await _certProvisioner.ProvisionAsync(_tvIp, _session.Auth!, SetStatus);

			// 3) Download the package and 4) install it (resign -> [permit] -> install).
			var wgtPath = await _installer.DownloadAsync(url, SetStatus);
			await _installer.InstallAsync(_tvIp, wgtPath, cert, SetStatus);

			ShowResult($"✓ Installed on {_tvName}.\nOpen the TV's Apps list to launch it.", success: true);
		}
		catch (TaskCanceledException)
		{
			ShowResult("Sign-in was cancelled.", success: false);
		}
		catch (Exception ex)
		{
			ShowResult($"Install failed:\n{ex.Message}", success: false);
		}
		finally
		{
			Busy.IsRunning = false;
			ProgressRow.IsVisible = false;
			InstallBtn.IsEnabled = true;
		}
	}

	private void SetStatus(string message) =>
		MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = message);

	private void ShowResult(string message, bool success)
	{
		ResultLabel.Text = message;
		ResultLabel.TextColor = success ? Colors.MediumSeaGreen : Colors.OrangeRed;
		ResultCard.IsVisible = true;
	}
}
