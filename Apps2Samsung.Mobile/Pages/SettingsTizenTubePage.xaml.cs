using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > App customization > TizenTube: the Cobalt proxy address rewrite.</summary>
public partial class SettingsTizenTubePage : ContentPage
{
	private bool _loaded;

	public SettingsTizenTubePage()
	{
		InitializeComponent();
		TopBar.Title = "TizenTube";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_loaded = false;
		TizenTubeProxySwitch.IsToggled = MobileSettings.TizenTubeProxyOverride;
		TizenTubeProxyHostEntry.Text = MobileSettings.TizenTubeProxyHost;
		TizenTubeProxyHostError.IsVisible = false;
		_loaded = true;
	}

	private void OnTizenTubeProxyToggled(object? sender, ToggledEventArgs e)
	{
		if (_loaded)
			MobileSettings.TizenTubeProxyOverride = e.Value;
	}

	// Empty means "the TV being installed to"; anything else must be a host[:port] or it isn't saved.
	private void OnTizenTubeProxyHostUnfocused(object? sender, FocusEventArgs e)
	{
		if (!_loaded)
			return;

		var host = TizenTubeProxyHostEntry.Text?.Trim() ?? string.Empty;
		var valid = host.Length == 0 || TizenTubeProxyPatcher.TryParseHost(host, out _, out _);
		TizenTubeProxyHostError.IsVisible = !valid;
		if (valid)
			MobileSettings.TizenTubeProxyHost = host;
	}
}
