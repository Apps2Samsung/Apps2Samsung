using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > App customization > ImmiTV: Immich server and login, pre-filled at install.</summary>
public partial class SettingsImmiTvPage : ContentPage
{
	private bool _loaded;

	public SettingsImmiTvPage()
	{
		InitializeComponent();
		TopBar.Title = "ImmiTV";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_loaded = false;
		ImmiTvServerEntry.Text = MobileSettings.ImmiTvServerUrl;
		ImmiTvEmailEntry.Text = MobileSettings.ImmiTvEmail;
		ImmiTvPasswordEntry.Text = MobileSettings.ImmiTvPassword;
		ImmiTvApiKeyEntry.Text = MobileSettings.ImmiTvApiKey;
		_loaded = true;
	}

	// One eye toggle covers both secrets (password + API key), like the GitHub token's.
	private void OnToggleImmiTvVisibility(object? sender, EventArgs e)
	{
		var hide = !ImmiTvPasswordEntry.IsPassword;
		ImmiTvPasswordEntry.IsPassword = hide;
		ImmiTvApiKeyEntry.IsPassword = hide;
		ImmiTvEyeBtn.Opacity = hide ? 1.0 : 0.5;
	}

	// Saves all four fields on any of them losing focus; the secrets go through the async
	// SecureStorage-backed setters.
	private async void OnImmiTvUnfocused(object? sender, FocusEventArgs e)
	{
		if (!_loaded)
			return;

		MobileSettings.ImmiTvServerUrl = ImmiTvServerEntry.Text ?? string.Empty;
		MobileSettings.ImmiTvEmail = ImmiTvEmailEntry.Text ?? string.Empty;
		await MobileSettings.SetImmiTvPasswordAsync(ImmiTvPasswordEntry.Text);
		await MobileSettings.SetImmiTvApiKeyAsync(ImmiTvApiKeyEntry.Text);
	}
}
