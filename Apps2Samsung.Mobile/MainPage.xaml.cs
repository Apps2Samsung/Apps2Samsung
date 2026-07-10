using System.Linq;
using System.Text;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Mobile;

public partial class MainPage : ContentPage
{
	private readonly INetworkService _networkService;

	public MainPage(INetworkService networkService)
	{
		InitializeComponent();
		_networkService = networkService;
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
}
