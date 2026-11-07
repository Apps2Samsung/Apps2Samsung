using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > Advanced: the GitHub token, extra TV device IDs and Skip Developer Mode checks (#1026).</summary>
public partial class SettingsAdvancedPage : ContentPage
{
	private bool _loaded;

	public SettingsAdvancedPage()
	{
		InitializeComponent();
		TopBar.Title = L10n.Get("lblAdvanced");
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_loaded = false;
		TokenEntry.Text = MobileSettings.GitHubToken;
		DuidsEditor.Text = MobileSettings.ManualDuids;
		SkipDevModeSwitch.IsToggled = MobileSettings.SkipDevModeChecks;
		_loaded = true;
	}

	private void OnToggleTokenVisibility(object? sender, EventArgs e)
	{
		TokenEntry.IsPassword = !TokenEntry.IsPassword;
		TokenEyeBtn.Opacity = TokenEntry.IsPassword ? 1.0 : 0.5;
	}

	private async void OnTokenUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			await MobileSettings.SetGitHubTokenAsync(TokenEntry.Text);
	}

	private void OnSkipDevModeToggled(object? sender, ToggledEventArgs e)
	{
		if (_loaded)
			MobileSettings.SkipDevModeChecks = e.Value;
	}

	private void OnDuidsUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.ManualDuids = DuidsEditor.Text ?? string.Empty;
	}
}
