using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apps2Samsung.Backup;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
	private bool _loaded;
	private readonly List<ChannelRow> _channelRows = new();

	private sealed record ChannelRow(View Container, Entry Name, Entry Url);

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
		PartnerSigningSwitch.IsToggled = MobileSettings.PartnerSigning;
		TryOverwriteSwitch.IsToggled = MobileSettings.TryOverwrite;
		ForceLoginSwitch.IsToggled = MobileSettings.ForceSamsungLogin;

		ChannelsContainer.Children.Clear();
		_channelRows.Clear();
		foreach (var channel in MobileSettings.GetTvAppChannels())
			AddChannelRow(channel.Name, channel.Url);

		_loaded = true;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async void OnAppIconsClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new AppIconsPage());

	private async void OnJellyfinClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new JellyfinSettingsPage());

	// Hands the current session's debug log to the OS share sheet. The file lives in private app
	// storage (FileSystem.AppDataDirectory/Logs) with no way to reach it otherwise, so this is how a
	// user gets logs off the phone when reporting an issue. Saving is set up in MauiProgram via FileLog.
	private async void OnShareLogClicked(object? sender, EventArgs e)
	{
		var logPath = Apps2Samsung.Diagnostics.FileLog.CurrentLogFile;
		if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
		{
			await DisplayAlert("Debug log", "No log file is available for this session yet.", "OK");
			return;
		}

		try
		{
			await Share.Default.RequestAsync(new ShareFileRequest
			{
				Title = "Apps2Samsung debug log",
				File = new ShareFile(logPath),
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert("Debug log", $"Couldn't share the log: {ex.Message}", "OK");
		}
	}

	// ---- Backup (export/import of settings + signing certificates) ----
	// Uses the head-agnostic Apps2Samsung.Backup.BackupService: the archive holds a settings.json
	// (desktop AppSettings schema, so configs move PC ↔ Mac ↔ mobile) plus the whole Certificate/ tree.

	private static string CertStorePath => new MobileAppConfig().CertificateStorePath;

	// Serialize the mobile settings into the shared desktop AppSettings JSON schema.
	private static string BuildSettingsJson()
	{
		var obj = new JsonObject
		{
			["DeletePreviousInstall"] = MobileSettings.DeletePreviousInstall,
			["OpenAfterInstall"] = MobileSettings.OpenAfterInstall,
			["KeepWGTFile"] = MobileSettings.KeepWgtFile,
			["ShowAllJellyfinVersions"] = MobileSettings.ShowAllJellyfinVersions,
			["ForceSamsungLogin"] = MobileSettings.ForceSamsungLogin,
			["TryOverwrite"] = MobileSettings.TryOverwrite,
			["PartnerSigning"] = MobileSettings.PartnerSigning,
			["ManualDuids"] = MobileSettings.ManualDuids,
			["GitHubToken"] = MobileSettings.GitHubToken,
			["JellyfinIP"] = MobileSettings.JellyfinServerUrl,
			["JellyfinUserId"] = MobileSettings.JellyfinUserId,
			["JellyfinServerId"] = MobileSettings.JellyfinServerId,
			["JellyfinServerName"] = MobileSettings.JellyfinServerName,
			["JellyfinServerLocalAddress"] = MobileSettings.JellyfinServerLocalAddress,
			["CustomCss"] = MobileSettings.JellyfinCustomCss,
			["PatchYoutubePlugin"] = MobileSettings.JellyfinPatchYoutube,
			["JellyfinAccessToken"] = MobileSettings.JellyfinAccessToken,
			["TvAppChannelsJson"] = MobileSettings.TvAppChannelsJson,
			["CustomAppIconsJson"] = MobileSettings.CustomAppIconsJson,
		};
		return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
	}

	private async void OnExportBackupClicked(object? sender, EventArgs e)
	{
		try
		{
			var path = Path.Combine(FileSystem.CacheDirectory, "apps2samsung-backup.zip");
			using (var stream = File.Create(path))
				BackupService.Export(stream, BuildSettingsJson(), CertStorePath);

			await Share.Default.RequestAsync(new ShareFileRequest("Apps2Samsung backup", new ShareFile(path)));
		}
		catch (Exception ex)
		{
			await DisplayAlert("Export backup", $"Couldn't export the backup: {ex.Message}", "OK");
		}
	}

	private async void OnImportBackupClicked(object? sender, EventArgs e)
	{
		try
		{
			// Accept .zip archives. FilePickerFileType is per-platform; on Android match by MIME + extension.
			var zipTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
			{
				[DevicePlatform.Android] = new[] { "application/zip", "application/octet-stream" },
				[DevicePlatform.iOS] = new[] { "public.zip-archive" },
				[DevicePlatform.MacCatalyst] = new[] { "public.zip-archive" },
				[DevicePlatform.WinUI] = new[] { ".zip" },
			});

			var result = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Select an Apps2Samsung backup (.zip)",
				FileTypes = zipTypes,
			});
			if (result is null)
				return;

			BackupImportResult import;
			using (var stream = await result.OpenReadAsync())
				import = BackupService.Import(stream, CertStorePath);

			if (!string.IsNullOrEmpty(import.SettingsJson))
				await ApplySettingsJsonAsync(import.SettingsJson!);

			// Refresh visible controls from the newly-applied settings.
			OnAppearing();

			await DisplayAlert(
				"Import backup",
				$"Imported settings + {import.CertificateFilesRestored} certificate file(s). Certificates are already restored. Restart the app to fully apply the imported settings.",
				"OK");
		}
		catch (Exception ex)
		{
			await DisplayAlert("Import backup", $"Couldn't import the backup: {ex.Message}", "OK");
		}
	}

	// Map the shared AppSettings JSON keys back onto MobileSettings. Missing keys are left untouched.
	private static async Task ApplySettingsJsonAsync(string json)
	{
		JsonNode? root;
		try { root = JsonNode.Parse(json); }
		catch { return; }
		if (root is not JsonObject o)
			return;

		bool? GetBool(string key)
		{
			try { return o[key]?.GetValue<bool>(); }
			catch { return null; }
		}
		string? GetString(string key)
		{
			try { return o[key]?.GetValue<string>(); }
			catch { return null; }
		}

		if (GetBool("DeletePreviousInstall") is { } deletePrev) MobileSettings.DeletePreviousInstall = deletePrev;
		if (GetBool("OpenAfterInstall") is { } openAfter) MobileSettings.OpenAfterInstall = openAfter;
		if (GetBool("KeepWGTFile") is { } keepWgt) MobileSettings.KeepWgtFile = keepWgt;
		if (GetBool("ShowAllJellyfinVersions") is { } showAll) MobileSettings.ShowAllJellyfinVersions = showAll;
		if (GetBool("ForceSamsungLogin") is { } forceLogin) MobileSettings.ForceSamsungLogin = forceLogin;
		if (GetBool("TryOverwrite") is { } tryOverwrite) MobileSettings.TryOverwrite = tryOverwrite;
		if (GetBool("PartnerSigning") is { } partner) MobileSettings.PartnerSigning = partner;
		if (GetBool("PatchYoutubePlugin") is { } patchYt) MobileSettings.JellyfinPatchYoutube = patchYt;

		if (GetString("ManualDuids") is { } duids) MobileSettings.ManualDuids = duids;
		if (GetString("JellyfinIP") is { } jfIp) MobileSettings.JellyfinServerUrl = jfIp;
		if (GetString("JellyfinUserId") is { } jfUser) MobileSettings.JellyfinUserId = jfUser;
		if (GetString("JellyfinServerId") is { } jfServer) MobileSettings.JellyfinServerId = jfServer;
		if (GetString("JellyfinServerName") is { } jfName) MobileSettings.JellyfinServerName = jfName;
		if (GetString("JellyfinServerLocalAddress") is { } jfLocal) MobileSettings.JellyfinServerLocalAddress = jfLocal;
		if (GetString("CustomCss") is { } css) MobileSettings.JellyfinCustomCss = css;
		if (GetString("TvAppChannelsJson") is { } channels) MobileSettings.TvAppChannelsJson = channels;
		if (GetString("CustomAppIconsJson") is { } icons) MobileSettings.CustomAppIconsJson = icons;

		// Secrets go through the async SecureStorage-backed setters.
		if (GetString("GitHubToken") is { } token) await MobileSettings.SetGitHubTokenAsync(token);
		if (GetString("JellyfinAccessToken") is { } accessToken) await MobileSettings.SetJellyfinAccessTokenAsync(accessToken);
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
		MobileSettings.PartnerSigning = PartnerSigningSwitch.IsToggled;
		MobileSettings.TryOverwrite = TryOverwriteSwitch.IsToggled;
		MobileSettings.ForceSamsungLogin = ForceLoginSwitch.IsToggled;
	}

	private void OnAddChannel(object? sender, EventArgs e) => AddChannelRow(string.Empty, string.Empty);

	private void AddChannelRow(string name, string url)
	{
		var nameEntry = new Entry { Text = name, Placeholder = "Name", BackgroundColor = Colors.Transparent };
		var urlEntry = new Entry { Text = url, Placeholder = "https://…/stream.m3u8", BackgroundColor = Colors.Transparent };
		var removeBtn = new Button
		{
			Text = "✕",
			FontSize = 14,
			Padding = 0,
			WidthRequest = 40,
			HeightRequest = 40,
			CornerRadius = 6,
			BackgroundColor = Colors.Transparent,
			BorderWidth = 1,
			BorderColor = Color.FromArgb("#CDD3D8"),
			TextColor = Color.FromArgb("#B00020"),
		};

		var grid = new Grid
		{
			ColumnSpacing = 8,
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(new GridLength(40)),
			},
		};
		Grid.SetColumn(nameEntry, 0);
		Grid.SetColumn(urlEntry, 1);
		Grid.SetColumn(removeBtn, 2);
		grid.Children.Add(nameEntry);
		grid.Children.Add(urlEntry);
		grid.Children.Add(removeBtn);

		var row = new ChannelRow(grid, nameEntry, urlEntry);
		nameEntry.Unfocused += (_, _) => SaveChannels();
		urlEntry.Unfocused += (_, _) => SaveChannels();
		removeBtn.Clicked += (_, _) =>
		{
			ChannelsContainer.Children.Remove(grid);
			_channelRows.Remove(row);
			SaveChannels();
		};

		_channelRows.Add(row);
		ChannelsContainer.Children.Add(grid);
	}

	private void SaveChannels()
	{
		if (!_loaded)
			return;

		// Persist as the {name,url} JSON shape the Core injector expects; skip blank rows.
		var channels = _channelRows
			.Select(r => new { name = r.Name.Text ?? string.Empty, url = r.Url.Text ?? string.Empty })
			.Where(c => !string.IsNullOrWhiteSpace(c.name) || !string.IsNullOrWhiteSpace(c.url))
			.ToList();

		MobileSettings.TvAppChannelsJson = JsonSerializer.Serialize(channels);
	}
}
