using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallCompletePage : ContentPage
{
	private const string KoFiUrl = "https://ko-fi.com/patrickst";

	public InstallCompletePage(string appName)
	{
		InitializeComponent();
		MessageLabel.Text = $"{appName} has been successfully installed!";

		// The Ko-fi QR is a bundled raw asset; Android renders webp natively from a stream.
		QrImage.Source = ImageSource.FromStream(_ => FileSystem.OpenAppPackageFileAsync("kofi_qr.webp"));
	}

	private async void OnKoFiClicked(object? sender, EventArgs e)
	{
		try { await Launcher.OpenAsync(KoFiUrl); }
		catch { /* no browser / cancelled */ }
		await Navigation.PopModalAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e) =>
		await Navigation.PopModalAsync();
}
