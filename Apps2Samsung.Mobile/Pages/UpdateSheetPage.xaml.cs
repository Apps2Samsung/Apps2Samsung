using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;
using Apps2Samsung.Update;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The update sheet. Android: downloads the APK into the app's cache with progress and opens it
/// with the package installer (the system then asks to confirm; the first time it also asks to
/// allow installs from this app). iOS: opens the release page.
/// </summary>
public partial class UpdateSheetPage : ContentPage
{
	private readonly UpdateCheckResult _update;
	private readonly UpdateDownloader _downloader = new();
	private CancellationTokenSource? _download;

	public UpdateSheetPage(UpdateCheckResult update)
	{
		InitializeComponent();
		_update = update;
		TitleLabel.Text = string.Format(L10n.Get("UpdateSheetAvailable"), update.LatestVersion);
		var sub = string.Format(L10n.Get("UpdateYouHave"), update.CurrentVersion);
		if (update.AssetSize is { } size)
			sub += " " + DownloadProgress.Megabytes(size);
		if (update.PublishedAt is { } date)
			sub += " · " + date.ToShortDateString();
		SubLabel.Text = sub;
		NotesLabel.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? update.ReleaseTitle : update.ReleaseNotes;

		var canInstall = OperatingSystem.IsAndroid() && !string.IsNullOrEmpty(update.DownloadUrl);
		PrimaryBtn.Text = L10n.Get(canInstall ? "UpdateDownloadInstall" : "UpdateOpenRelease");
		if (!OperatingSystem.IsAndroid())
			ShowError(L10n.Get("UpdateNotOnThisPlatform"), isError: false);
	}

	private async void OnPrimaryClicked(object? sender, EventArgs e)
	{
		if (!OperatingSystem.IsAndroid() || string.IsNullOrEmpty(_update.DownloadUrl))
		{
			try { await Launcher.Default.OpenAsync(_update.ReleasesPageUrl); }
			catch (Exception ex) { ShowError(ex.Message, true); }
			return;
		}
		await DownloadAndInstallAsync();
	}

	private async Task DownloadAndInstallAsync()
	{
		if (_download is not null)
			return;
		_download = new CancellationTokenSource();
		ErrorNotice.IsVisible = false;
		ProgressSection.IsVisible = true;
		Progress.Progress = 0;
		ProgressLabel.Text = L10n.Get("UpdateDownloading");
		PrimaryBtn.IsEnabled = false;
		SkipBtn.IsEnabled = false;

		var fileName = Path.GetFileName(new Uri(_update.DownloadUrl!).LocalPath);
		var path = Path.Combine(FileSystem.CacheDirectory, "updates", fileName);
		try
		{
			var progress = new Progress<DownloadProgress>(p =>
			{
				Progress.Progress = p.Percent / 100.0;
				ProgressLabel.Text = p.Text;
			});
			await _downloader.DownloadAsync(_update.DownloadUrl!, path, progress, _download.Token);

			ProgressLabel.Text = L10n.Get("UpdateOpeningInstaller");
			// The package installer, through the app's FileProvider: no platform code, and the
			// system asks for the install confirmation (and, once, for the permission).
			await Launcher.Default.OpenAsync(new OpenFileRequest(_update.LatestVersion, new ReadOnlyFile(path, "application/vnd.android.package-archive")));
			PrimaryBtn.IsEnabled = true;
		}
		catch (OperationCanceledException)
		{
			ProgressSection.IsVisible = false;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[update] download/install failed: {ex}");
			ShowError(string.Format(L10n.Get("UpdateDownloadFailed"), ex.Message), true);
			ProgressSection.IsVisible = false;
			PrimaryBtn.IsEnabled = true;
		}
		finally
		{
			SkipBtn.IsEnabled = true;
			_download?.Dispose();
			_download = null;
		}
	}

	private void ShowError(string text, bool isError)
	{
		ErrorLabel.Text = text;
		ErrorLabel.TextColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(isError ? "Bad" : "Muted");
		ErrorNotice.Stroke = new SolidColorBrush(Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(isError ? "Bad" : "Hair"));
		ErrorNotice.BackgroundColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(isError ? "BadSoft" : "Surface");
		ErrorNotice.IsVisible = true;
	}

	private async void OnSkipClicked(object? sender, EventArgs e)
	{
		MobileSettings.SkippedUpdateVersion = _update.LatestVersion;
		await Navigation.PopModalAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e)
	{
		_download?.Cancel();
		await Navigation.PopModalAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_download?.Cancel();
	}
}
