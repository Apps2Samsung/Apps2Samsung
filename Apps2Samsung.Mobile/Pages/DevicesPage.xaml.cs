using System.Collections.ObjectModel;
using System.Linq;
using Apps2Samsung.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

public partial class DevicesPage : ContentPage
{
	private readonly INetworkService _networkService;
	private readonly IServiceProvider _services;
	private readonly ObservableCollection<DeviceItem> _devices = new();
	private bool _scannedOnce;

	public DevicesPage(INetworkService networkService, IServiceProvider services)
	{
		InitializeComponent();
		_networkService = networkService;
		_services = services;
		DevicesView.ItemsSource = _devices;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (!_scannedOnce)
			await ScanAsync();
	}

	private async void OnScanClicked(object? sender, EventArgs e) => await ScanAsync();

	private async void OnRefreshing(object? sender, EventArgs e)
	{
		await ScanAsync();
		Refresh.IsRefreshing = false;
	}

	private async Task ScanAsync()
	{
		_scannedOnce = true;
		ScanBtn.IsEnabled = false;
		ScanBtn.Text = "Scanning…";
		EmptyLabel.Text = "Scanning the network…";
		try
		{
			var found = (await _networkService.GetLocalTizenAddresses())
				.OrderByDescending(d => d.DebugPortOpen)
				.Select(d => new DeviceItem
				{
					IpAddress = d.IpAddress,
					Name = string.IsNullOrWhiteSpace(d.DeviceName)
						? (string.IsNullOrWhiteSpace(d.Manufacturer) ? "Samsung TV" : d.Manufacturer!)
						: d.DeviceName!,
					IsReady = d.DebugPortOpen,
				})
				.ToList();

			_devices.Clear();
			foreach (var d in found)
				_devices.Add(d);

			EmptyLabel.Text = "No TVs found.";
		}
		catch (Exception ex)
		{
			await DisplayAlert("Scan failed", ex.Message, "OK");
		}
		finally
		{
			ScanBtn.IsEnabled = true;
			ScanBtn.Text = "Scan again";
		}
	}

	private async void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is not DeviceItem item)
			return;

		DevicesView.SelectedItem = null; // allow re-selecting the same row later

		if (!item.IsReady)
		{
			await DisplayAlert(item.Name,
				"This TV answered but its developer/debug port isn't open yet. Enable Developer Mode on the TV (and check its Host PC IP), then scan again.",
				"OK");
			return;
		}

		var installPage = _services.GetRequiredService<InstallPage>();
		installPage.SetTarget(item.IpAddress, item.Name);
		await Navigation.PushAsync(installPage);
	}
}
