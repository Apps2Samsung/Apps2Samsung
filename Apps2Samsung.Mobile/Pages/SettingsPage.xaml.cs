using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
	private bool _loaded;

	public SettingsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		// Load current values without firing the change handlers.
		_loaded = false;
		TokenEntry.Text = MobileSettings.GitHubToken;
		DuidsEditor.Text = MobileSettings.ManualDuids;
		RemoveOldSwitch.IsToggled = MobileSettings.DeletePreviousInstall;
		OpenAfterSwitch.IsToggled = MobileSettings.OpenAfterInstall;
		KeepWgtSwitch.IsToggled = MobileSettings.KeepWgtFile;
		ShowAllJfSwitch.IsToggled = MobileSettings.ShowAllJellyfinVersions;
		_loaded = true;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

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

	private void OnDuidsUnfocused(object? sender, FocusEventArgs e)
	{
		if (_loaded)
			MobileSettings.ManualDuids = DuidsEditor.Text ?? string.Empty;
	}

	private void OnToggle(object? sender, ToggledEventArgs e)
	{
		if (!_loaded)
			return;

		MobileSettings.DeletePreviousInstall = RemoveOldSwitch.IsToggled;
		MobileSettings.OpenAfterInstall = OpenAfterSwitch.IsToggled;
		MobileSettings.KeepWgtFile = KeepWgtSwitch.IsToggled;
		MobileSettings.ShowAllJellyfinVersions = ShowAllJfSwitch.IsToggled;
	}
}
