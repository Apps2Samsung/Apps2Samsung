using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The Settings hub (#755): the update card and Beta toggle, then a row per area that pushes its own
/// page. The pages themselves hold the settings; this one only summarises them.
/// </summary>
public partial class SettingsPage : ContentPage
{
	private bool _loaded;

	public SettingsPage()
	{
		InitializeComponent();
		AppVersionLabel.Text = $"Apps2Samsung {AppInfo.Current.VersionString}";
		// Shows the app version (ApplicationDisplayVersion), so it stays in sync with the build.
		VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		// Load current values without firing the change handlers; a sub-page may have changed them.
		_loaded = false;
		BetaUpdatesSwitch.IsToggled = MobileSettings.IncludeBetaUpdates;
		InstallingSummary.Text = CertificateName(MobileSettings.CertificatePreference);
		LanguageSummary.Text = L10n.GetDisplayName(L10n.CurrentLanguage);
		ShowUpdateState();
		_loaded = true;
	}

	/// <summary>The certificate choice as the Installing page's switch names it.</summary>
	internal static string CertificateName(string preference) =>
		preference == MobileSettings.CertificatePreferenceAuto ? L10n.Get("lblCertificateAutomatic") : preference;

	// Where the app stands, from the last update check that got an answer (the quiet one at startup
	// or a manual one): up to date, a newer version, or not checked yet.
	private void ShowUpdateState()
	{
		var result = IPlatformApplication.Current?.Services.GetService<UpdateFlow>()?.LastResult;
		string? dot = null;
		if (result is null)
			UpdateStateLabel.Text = L10n.Get("lblUpdateNotChecked");
		else if (result.IsUpdateAvailable)
		{
			UpdateStateLabel.Text = string.Format(L10n.Get("lblUpdateAvailableShort"), result.LatestVersion);
			dot = "Warn";
		}
		else
		{
			UpdateStateLabel.Text = L10n.Get("lblUpToDate");
			dot = "Ok";
		}
		UpdateDot.IsVisible = dot is not null;
		if (dot is not null)
			UpdateDot.Fill = new SolidColorBrush(DesignTheme.ColorOf(dot));
	}

	// The manual update check: every outcome is reported, and a skipped version is offered again.
	private async void OnCheckUpdatesClicked(object? sender, EventArgs e)
	{
		CheckUpdatesBtn.IsEnabled = false;
		UpdateStateLabel.Text = L10n.Get("UpdateChecking");
		try
		{
			var updates = IPlatformApplication.Current!.Services.GetRequiredService<UpdateFlow>();
			await updates.RunAsync(this, manual: true);
		}
		finally
		{
			CheckUpdatesBtn.IsEnabled = true;
			ShowUpdateState();
		}
	}

	private void OnBetaToggled(object? sender, ToggledEventArgs e)
	{
		if (_loaded)
			MobileSettings.IncludeBetaUpdates = e.Value;
	}

	private async void OnInstallingTapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new SettingsInstallPage());

	private async void OnAppsTapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new SettingsAppsPage());

	private async void OnLanguageTapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new SettingsLanguagePage());

	private async void OnBackupTapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new SettingsBackupPage());

	private async void OnAdvancedTapped(object? sender, TappedEventArgs e) => await Navigation.PushAsync(new SettingsAdvancedPage());
}
