using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apps2Samsung.Backup;
using Apps2Samsung.Certificate;
using Apps2Samsung.Collections;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
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
		LoadLanguages();
		TokenEntry.Text = MobileSettings.GitHubToken;
		DuidsEditor.Text = MobileSettings.ManualDuids;
		RemoveOldSwitch.IsToggled = MobileSettings.DeletePreviousInstall;
		OpenAfterSwitch.IsToggled = MobileSettings.OpenAfterInstall;
		KeepWgtSwitch.IsToggled = MobileSettings.KeepWgtFile;
		ShowAllJfSwitch.IsToggled = MobileSettings.ShowAllJellyfinVersions;
		BetaUpdatesSwitch.IsToggled = MobileSettings.IncludeBetaUpdates;
		LoadCertificatePicker();
		TryOverwriteSwitch.IsToggled = MobileSettings.TryOverwrite;
		ForceLoginSwitch.IsToggled = MobileSettings.ForceSamsungLogin;

		ChannelsContainer.Children.Clear();
		_channelRows.Clear();
		foreach (var channel in MobileSettings.GetTvAppChannels())
			AddChannelRow(channel.Name, channel.Url);

		_loaded = true;
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async void OnAppIconsClicked(object? sender, EventArgs e) =>
		await Navigation.PushAsync(IPlatformApplication.Current!.Services.GetRequiredService<AppIconsPage>());

	private async void OnJellyfinClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new JellyfinSettingsPage());

	// Hands the current session's debug log to the OS share sheet. The file lives in private app
	// storage (FileSystem.AppDataDirectory/Logs) with no way to reach it otherwise, so this is how a
	// user gets logs off the phone when reporting an issue. Saving is set up in MauiProgram via FileLog.
	private async void OnShareLogClicked(object? sender, EventArgs e)
	{
		var logPath = Apps2Samsung.Diagnostics.FileLog.CurrentLogFile;
		if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
		{
			await DisplayAlert(L10n.Get("lblDebugLog"), L10n.Get("statusNoLogFile"), L10n.Get("btn_Close"));
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
			await DisplayAlert(L10n.Get("lblDebugLog"), string.Format(L10n.Get("statusLogShareFailed"), ex.Message), L10n.Get("btn_Close"));
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
			await DisplayAlert(
				L10n.Get("lblExportBackup"),
				string.Format(L10n.Get("statusExportBackupFailed"), ErrorText.Describe(ex, "backup/export")),
				L10n.Get("lblOk"));
		}
	}

	private async void OnImportBackupClicked(object? sender, EventArgs e)
	{
		try
		{
			// Some file managers hand a .zip to the picker as application/octet-stream.
			var picked = await SafFilePicker.PickAsync("application/zip", "application/octet-stream");
			if (picked is null)
				return;

			BackupImportResult import;
			using (var stream = File.OpenRead(picked.LocalPath))
				import = BackupService.Import(stream, CertStorePath);

			if (!string.IsNullOrEmpty(import.SettingsJson))
				await ApplySettingsJsonAsync(import.SettingsJson!);

			// Make the imported certificate the selected one.
			DefaultCertificateToImportedProfile();

			// Refresh visible controls from the newly-applied settings.
			OnAppearing();

			await DisplayAlert(
				L10n.Get("lblImportBackup"),
				string.Format(L10n.Get("statusImportedMobile"), import.CertificateFilesRestored),
				L10n.Get("lblOk"));
		}
		catch (Exception ex)
		{
			await DisplayAlert(
				L10n.Get("lblImportBackup"),
				string.Format(L10n.Get("statusImportBackupFailed"), ErrorText.Describe(ex, "backup/import"))
					+ "\n\n" + L10n.Get("statusSeeDebugLog"),
				L10n.Get("lblOk"));
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
		MobileSettings.IncludeBetaUpdates = BetaUpdatesSwitch.IsToggled;
		MobileSettings.TryOverwrite = TryOverwriteSwitch.IsToggled;
		MobileSettings.ForceSamsungLogin = ForceLoginSwitch.IsToggled;
	}

	// Populates the certificate picker (Automatic / Public / Partner) and selects the current preference.
	private void LoadCertificatePicker()
	{
		CertificatePicker.ItemsSource ??= new List<string>
		{
			MobileSettings.CertificatePreferenceAuto,
			MobileSettings.CertificatePreferencePublic,
			MobileSettings.CertificatePreferencePartner,
		};
		CertificatePicker.SelectedItem = MobileSettings.CertificatePreference;
	}

	private void OnCertificateChanged(object? sender, EventArgs e)
	{
		if (!_loaded)
			return;
		if (CertificatePicker.SelectedItem is string pref)
			MobileSettings.CertificatePreference = pref;
	}

	// After an import, default the certificate picker to whichever level profile was restored — so the
	// imported certificate becomes the selected one (when exactly one level is present).
	private static void DefaultCertificateToImportedProfile()
	{
		var store = CertStorePath;
		bool Has(CertificatePrivilegeLevel level) =>
			CertificateProvisioningService.HasUsableAuthorCert(
				Path.Combine(store, CertificateProvisioningService.ProfileName(level)));

		bool partner = Has(CertificatePrivilegeLevel.Partner);
		bool pub = Has(CertificatePrivilegeLevel.Public);

		if (partner && !pub)
			MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePartner;
		else if (pub && !partner)
			MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePublic;
		// If both or neither were imported, leave the preference as-is (Automatic covers "both").
	}

	private void OnAddChannel(object? sender, EventArgs e) => AddChannelRow(string.Empty, string.Empty);

	private void AddChannelRow(string name, string url)
	{
		var nameEntry = new Entry { Text = name, Placeholder = "Name", BackgroundColor = Colors.Transparent };
		var urlEntry = new Entry { Text = url, Placeholder = "https://…/stream.m3u8", BackgroundColor = Colors.Transparent };
		var upBtn = SmallChannelButton("↑", "#2C3E50");
		var downBtn = SmallChannelButton("↓", "#2C3E50");
		var removeBtn = SmallChannelButton("✕", "#B00020");

		// Row 0: name + url. Row 1: reorder (↑ ↓) + remove, right-aligned, so the entries stay wide.
		var grid = new Grid
		{
			ColumnSpacing = 8,
			RowSpacing = 2,
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
			},
		};
		Grid.SetColumn(nameEntry, 0); Grid.SetRow(nameEntry, 0);
		Grid.SetColumn(urlEntry, 1); Grid.SetRow(urlEntry, 0);

		var buttons = new HorizontalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.End };
		buttons.Children.Add(upBtn);
		buttons.Children.Add(downBtn);
		buttons.Children.Add(removeBtn);
		Grid.SetColumn(buttons, 0); Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2);

		grid.Children.Add(nameEntry);
		grid.Children.Add(urlEntry);
		grid.Children.Add(buttons);

		var row = new ChannelRow(grid, nameEntry, urlEntry);
		nameEntry.Unfocused += (_, _) => SaveChannels();
		urlEntry.Unfocused += (_, _) => SaveChannels();
		// Channels play on the TV in list order, so let the user reorder them (mirrors the desktop head).
		upBtn.Clicked += (_, _) => MoveChannelRow(row, -1);
		downBtn.Clicked += (_, _) => MoveChannelRow(row, +1);
		removeBtn.Clicked += (_, _) =>
		{
			ChannelsContainer.Children.Remove(grid);
			_channelRows.Remove(row);
			SaveChannels();
		};

		_channelRows.Add(row);
		ChannelsContainer.Children.Add(grid);
	}

	private static Button SmallChannelButton(string text, string textColor) => new()
	{
		Text = text,
		FontSize = 14,
		Padding = 0,
		WidthRequest = 40,
		HeightRequest = 40,
		CornerRadius = 6,
		BackgroundColor = Colors.Transparent,
		BorderWidth = 1,
		BorderColor = Color.FromArgb("#CDD3D8"),
		TextColor = Color.FromArgb(textColor),
	};

	private void MoveChannelRow(ChannelRow row, int delta)
	{
		var i = _channelRows.IndexOf(row);
		// Shared bounds rule with the desktop head (Core Collections.ListReorder).
		if (ListReorder.TargetIndex(_channelRows.Count, i, delta) is not int j)
			return;

		_channelRows.RemoveAt(i);
		_channelRows.Insert(j, row);

		// Rebuild the visual order to match, then persist.
		ChannelsContainer.Children.Clear();
		foreach (var r in _channelRows)
			ChannelsContainer.Children.Add(r.Container);

		SaveChannels();
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

	// The picker lists the languages that actually have a translation file, shown by their own
	// endonym where .NET knows one ("Nederlands", not "nl") so the list is readable to the person
	// looking for their language.
	private readonly List<string> _languageCodes = new();

	private void LoadLanguages()
	{
		if (_languageCodes.Count > 0)
			return;

		_languageCodes.AddRange(L10n.AvailableLanguages);
		LanguagePicker.ItemsSource = _languageCodes.Select(DisplayName).ToList();
		LanguagePicker.SelectedIndex = _languageCodes.IndexOf(L10n.CurrentLanguage);
	}

	private static string DisplayName(string code)
	{
		try
		{
			var name = new System.Globalization.CultureInfo(code).NativeName;
			return string.IsNullOrWhiteSpace(name) ? code : char.ToUpperInvariant(name[0]) + name[1..];
		}
		catch (System.Globalization.CultureNotFoundException)
		{
			return code;
		}
	}

	private async void OnLanguageChanged(object? sender, EventArgs e)
	{
		if (!_loaded || LanguagePicker.SelectedIndex < 0 || LanguagePicker.SelectedIndex >= _languageCodes.Count)
			return;

		var code = _languageCodes[LanguagePicker.SelectedIndex];
		if (code == L10n.CurrentLanguage)
			return;

		L10n.SetLanguage(code);

		// {l:Localize} resolves while a page is being built, so every page already on the stack - this
		// one and the installer page under it - still holds the old language. Asking the user to
		// restart didn't help: Android keeps the process alive when the app is closed and reopened, so
		// the same page objects came back. Rebuild the stack instead, and land the user back on this
		// page so they can see the switch took.

		// Let the picker finish closing before the tree it lives in is swapped out from under it.
		await Task.Yield();

		var services = IPlatformApplication.Current!.Services;
		var window = Application.Current?.Windows.FirstOrDefault();
		if (window is null)
			return;

		var root = new NavigationPage(services.GetRequiredService<InstallerPage>());
		window.Page = root;
		await root.Navigation.PushAsync(new SettingsPage(), animated: false);
	}
}

