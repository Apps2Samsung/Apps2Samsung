using Apps2Samsung.Certificate;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > Installing: the signing certificate and the options every install follows.</summary>
public partial class SettingsInstallPage : ContentPage
{
	private bool _loaded;

	public SettingsInstallPage()
	{
		InitializeComponent();
		// Public and Partner are Samsung's names for the two certificate levels.
		CertPublic.Text = MobileSettings.CertificatePreferencePublic;
		CertPartner.Text = MobileSettings.CertificatePreferencePartner;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		// Load current values without firing the change handlers.
		_loaded = false;
		ShowCertificate(MobileSettings.CertificatePreference);
		ForceLoginSwitch.IsToggled = MobileSettings.ForceSamsungLogin;
		RemoveOldSwitch.IsToggled = MobileSettings.DeletePreviousInstall;
		TryOverwriteSwitch.IsToggled = MobileSettings.TryOverwrite;
		OpenAfterSwitch.IsToggled = MobileSettings.OpenAfterInstall;
		KeepWgtSwitch.IsToggled = MobileSettings.KeepWgtFile;
		AdultSwitch.IsToggled = MobileSettings.ShowAdultApps;
		_loaded = true;
	}

	// Automatic / Public / Partner, the one in force on the surface colour.
	private void ShowCertificate(string preference)
	{
		CertAuto.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferenceAuto);
		CertPublic.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferencePublic);
		CertPartner.BackgroundColor = Segment(preference == MobileSettings.CertificatePreferencePartner);
	}

	private static Color Segment(bool on) => on ? DesignTheme.ColorOf("Surface") : Colors.Transparent;

	private void OnCertificateClicked(object? sender, EventArgs e)
	{
		var preference = sender == CertPublic ? MobileSettings.CertificatePreferencePublic
			: sender == CertPartner ? MobileSettings.CertificatePreferencePartner
			: MobileSettings.CertificatePreferenceAuto;
		MobileSettings.CertificatePreference = preference;
		ShowCertificate(preference);
	}

	private void OnToggle(object? sender, ToggledEventArgs e)
	{
		if (!_loaded)
			return;

		MobileSettings.ForceSamsungLogin = ForceLoginSwitch.IsToggled;
		MobileSettings.DeletePreviousInstall = RemoveOldSwitch.IsToggled;
		MobileSettings.TryOverwrite = TryOverwriteSwitch.IsToggled;
		MobileSettings.OpenAfterInstall = OpenAfterSwitch.IsToggled;
		MobileSettings.KeepWgtFile = KeepWgtSwitch.IsToggled;
	}

	// Show adult (18+) apps (#850): turning it on asks "I am 18 or older" first; declined, the switch
	// springs back and nothing is stored. The Install tab refilters its list the next time it shows.
	private async void OnAdultToggled(object? sender, ToggledEventArgs e)
	{
		if (!_loaded)
			return;
		if (!e.Value)
		{
			MobileSettings.ShowAdultApps = false;
			return;
		}
		var confirmed = await DisplayAlert(
			L10n.Get("lblAdultConfirmTitle"), L10n.Get("lblAdultConfirmBody"), L10n.Get("lblAdultConfirmYes"), L10n.Get("keyNo"));
		if (confirmed)
		{
			MobileSettings.ShowAdultApps = true;
			return;
		}
		_loaded = false;
		AdultSwitch.IsToggled = false;
		_loaded = true;
	}

	// Makes the certificate for the selected TV now, so the first install doesn't stop for a
	// Samsung sign-in. Same flow as an install's certificate step, sheet included.
	private async void OnPrepareClicked(object? sender, EventArgs e)
	{
		var services = IPlatformApplication.Current!.Services;
		var tv = services.GetRequiredService<TvSelection>();
		if (!tv.HasTv)
		{
			await DisplayAlert(L10n.Get("lblStepCertificate"), L10n.Get("statusSelectTvFirst"), L10n.Get("btn_Close"));
			return;
		}

		PrepareBtn.IsEnabled = false;
		PrepareBtn.Text = L10n.Get("statusReadingTvDuid");
		try
		{
			var (_, created) = await services.GetRequiredService<CertificateFlow>()
				.RunAsync(Navigation, tv.Ip!, tv.DisplayName, false, null, new CancellationTokenSource());
			await DisplayAlert(
				L10n.Get("lblStepCertificate"),
				string.Format(L10n.Get(created ? "statusCertificatePrepared" : "statusCertificateAlreadyReady"), tv.DisplayName),
				L10n.Get("btn_Close"));
		}
		catch (OperationCanceledException)
		{
			// Cancel on the sheet, or Samsung's sign-in page closed: nothing to report.
		}
		catch (SamsungAccountEmailMissingException)
		{
			await Navigation.PushModalAsync(new AccountEmailSheetPage());
		}
		catch (Exception ex)
		{
			// The sheet shows the reason when it got that far; before it (reading the TV) it didn't open.
			if (!Navigation.ModalStack.OfType<CertificateSheetPage>().Any())
				await DisplayAlert(L10n.Get("lblStepCertificate"), ErrorText.Describe(ex, "certificate"), L10n.Get("btn_Close"));
		}
		finally
		{
			PrepareBtn.IsEnabled = true;
			PrepareBtn.Text = L10n.Get("lblPrepareCertificateNow");
		}
	}
}
