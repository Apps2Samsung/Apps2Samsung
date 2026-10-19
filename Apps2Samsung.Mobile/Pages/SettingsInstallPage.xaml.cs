using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;

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
}
