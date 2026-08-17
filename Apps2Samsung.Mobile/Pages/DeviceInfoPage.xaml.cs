using System;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Shows a TV's details (DUID, Tizen version, developer mode/IP, IP, …) gathered by the shared Core
/// <see cref="TizenDeviceInfoService"/> — the same data the desktop head shows.
/// </summary>
public partial class DeviceInfoPage : ContentPage
{
	private readonly ISdbEngine _sdb;
	private readonly string _tvIp;
	private readonly string _tvLabel;
	private readonly bool _debugPortOpen;

	public DeviceInfoPage(ISdbEngine sdb, string tvIp, string tvLabel, bool debugPortOpen)
	{
		InitializeComponent();
		_sdb = sdb;
		_tvIp = tvIp;
		_tvLabel = tvLabel;
		_debugPortOpen = debugPortOpen;
		CountLabel.Text = tvLabel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

	private async Task LoadAsync()
	{
		SetBusy(true, "Reading TV information…");
		try
		{
			var info = await TizenDeviceInfoService.GatherAsync(_sdb, _tvIp, _debugPortOpen);
			RowsList.ItemsSource = info.Rows;
			CountLabel.Text = _tvLabel;
		}
		catch (Exception ex)
		{
			RowsList.ItemsSource = null;
			EmptyLabel.Text = $"Couldn't read TV information: {ex.Message}";
			CountLabel.Text = _tvLabel;
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void SetBusy(bool busy, string? status = null)
	{
		Busy.IsVisible = busy;
		Busy.IsRunning = busy;
		RowsList.IsVisible = !busy;
		if (status is not null)
			CountLabel.Text = status;
	}
}
