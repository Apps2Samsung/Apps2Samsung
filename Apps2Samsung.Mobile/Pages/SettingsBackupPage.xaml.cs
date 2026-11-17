using System.Text.Json;
using System.Text.Json.Nodes;
using Apps2Samsung.Backup;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > Backup and diagnostics: settings + certificates out and in, and the debug log.</summary>
public partial class SettingsBackupPage : ContentPage
{
	public SettingsBackupPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		CertificateBackups.StatusChanged += OnBackupStatusChanged;
		ShowCertificateBackups();
		Apps2Samsung.Catalog.SavedTvs.Changed += OnSavedTvsChanged;
		ShowSavedTvs();
	}

	protected override void OnDisappearing()
	{
		CertificateBackups.StatusChanged -= OnBackupStatusChanged;
		Apps2Samsung.Catalog.SavedTvs.Changed -= OnSavedTvsChanged;
		base.OnDisappearing();
	}

	// ---- Saved TVs (#1069) ----

	private void OnSavedTvsChanged() => MainThread.BeginInvokeOnMainThread(ShowSavedTvs);

	private void ShowSavedTvs()
	{
		var tvs = Apps2Samsung.Catalog.SavedTvs.All().OrderByDescending(t => t.LastSeenUtc).ToList();
		SavedTvsNoneCard.IsVisible = tvs.Count == 0;
		SavedTvsCard.IsVisible = tvs.Count > 0;
		SavedTvRows.Children.Clear();
		var first = true;
		foreach (var tv in tvs)
		{
			if (!first)
				SavedTvRows.Children.Add(new BoxView { Style = StyleOf("RowDivider") });
			first = false;

			var p = tv.Profile;
			var title = string.IsNullOrWhiteSpace(p.TvName) ? tv.Key : p.TvName!;
			var name = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
			name.Children.Add(new Label { Text = title, FontSize = 16, LineBreakMode = LineBreakMode.TailTruncation });
			name.Children.Add(new Label
			{
				Text = (p.Apps.Count == 1 ? L10n.Get("lblSavedTvAppsOne") : string.Format(L10n.Get("lblSavedTvAppsMany"), p.Apps.Count))
					+ " · " + string.Format(L10n.Get("lblSavedTvSavedOn"), p.SavedAtUtc.ToLocalTime().ToString("d MMMM yyyy")),
				Style = StyleOf("RowHint"),
			});

			var grid = new Grid
			{
				ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
				ColumnSpacing = 12,
				Padding = new Thickness(16, 10),
				MinimumHeightRequest = 52,
			};
			grid.Add(name, 0);
			if (tv.Missing.Count > 0)
				grid.Add(new Border
				{
					StrokeThickness = 0,
					StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
					Padding = new Thickness(9, 4),
					VerticalOptions = LayoutOptions.Center,
					BackgroundColor = Token("WarnSoft"),
					Content = new Label
					{
						Text = tv.Missing.Count == 1 ? L10n.Get("lblSavedTvMissingOne") : string.Format(L10n.Get("lblSavedTvMissingMany"), tv.Missing.Count),
						FontSize = 12,
						FontAttributes = FontAttributes.Bold,
					},
				}, 1);
			var key = tv.Key;
			var tap = new TapGestureRecognizer();
			tap.Tapped += async (_, _) => await OnSavedTvTappedAsync(key, title);
			grid.GestureRecognizers.Add(tap);
			SavedTvRows.Children.Add(grid);
		}
	}

	// A row: restore it onto the selected TV, share the list as a file, or forget it.
	private async Task OnSavedTvTappedAsync(string key, string title)
	{
		var restore = L10n.Get("lblSavedTvRestoreOther");
		var share = L10n.Get("lblSavedTvShare");
		var forget = L10n.Get("lblSavedTvForget");
		var choice = await DisplayActionSheet(title, L10n.Get("lblCancel"), forget, restore, share);
		if (Apps2Samsung.Catalog.SavedTvs.Get(key) is not { } tv)
			return;
		if (choice == restore)
		{
			await Apps2Samsung.Mobile.Services.RestoreFlow.OpenOnTvTabAsync(tv.Profile);
		}
		else if (choice == share)
		{
			try
			{
				var safe = string.Concat(title.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c));
				var path = Path.Combine(FileSystem.CacheDirectory, $"{safe}-{tv.Profile.SavedAtUtc.ToLocalTime():yyyy-MM-dd}.a2stv");
				File.WriteAllText(path, Apps2Samsung.Catalog.TvProfiles.Serialize(tv.Profile));
				await Share.Default.RequestAsync(new ShareFileRequest(L10n.Get("lblSavedTvShareTitle"), new ShareFile(path)));
			}
			catch (Exception ex)
			{
				await DisplayAlert(share, ErrorText.Describe(ex, "savedtv/share"), L10n.Get("lblOk"));
			}
		}
		else if (choice == forget)
		{
			if (await DisplayAlert(forget, string.Format(L10n.Get("lblSavedTvForgetAsk"), title), forget, L10n.Get("lblCancel")))
				Apps2Samsung.Catalog.SavedTvs.Forget(key);
		}
	}

	// A list saved here, on a computer or by the CLI (tv save).
	private async void OnRestoreTvFromFileClicked(object? sender, EventArgs e)
	{
		try
		{
			// File managers hand an unknown extension to the picker as application/octet-stream.
			var picked = await LocalFilePicker.PickAsync("application/json", "application/octet-stream", "text/plain");
			if (picked is null)
				return;
			var profile = Apps2Samsung.Catalog.TvProfiles.Parse(File.ReadAllText(picked.LocalPath));
			await Apps2Samsung.Mobile.Services.RestoreFlow.OpenOnTvTabAsync(profile);
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblSavedTvPick"), ex is FormatException ? ex.Message : ErrorText.Describe(ex, "savedtv/pick"), L10n.Get("lblOk"));
		}
	}

	private void OnBackupStatusChanged() => MainThread.BeginInvokeOnMainThread(ShowCertificateBackups);

	// ---- Automatic certificate backups (#1068) ----

	private static Color Token(string token) => Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token);

	private static Style? StyleOf(string key) =>
		Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Style : null;

	private void ShowCertificateBackups()
	{
		CertificateBackupStatus? status;
		try { status = CertificateBackups.Status(); }
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[CertBackup] Status failed: {ex.Message}");
			status = null;
		}

		var has = status is { Profiles.Count: > 0 };
		CertNoneCard.IsVisible = !has;
		CertStatusCard.IsVisible = CertRowsCard.IsVisible = has;
		CertLocation.IsVisible = status is not null;
		if (status is null)
			return;

		CertLocation.Text = string.Format(L10n.Get("hintCertBackupGoesTo"), status.DisplayLocation, CertificateBackups.Keep);
		if (!has)
			return;

		var missing = status.NotBackedUp.Count();
		var safe = missing == 0;
		CertStatusCard.BackgroundColor = Token(safe ? "OkSoft" : "WarnSoft");
		CertStatusTile.BackgroundColor = Token(safe ? "Ok" : "Warn");
		CertTick.IsVisible = safe;
		CertBang.IsVisible = !safe;
		CertStatusTitle.Text = safe
			? L10n.Get("lblCertBackupSafe")
			: missing == 1 ? L10n.Get("lblCertBackupMissingOne") : string.Format(L10n.Get("lblCertBackupMissingMany"), missing);
		var text = safe && status.Latest is { } latest
			? string.Format(L10n.Get("lblCertBackupSafeHint"), latest.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy, HH:mm"))
			  + " " + L10n.Get("hintCertBackupPhoneCopy")
			: L10n.Get("lblCertBackupMissingHint");
		if (status.LastError is { } error)
			text += "\n" + string.Format(L10n.Get("lblCertBackupFailed"), error);
		CertStatusText.Text = text;

		CertBackUpNowButton.IsVisible = !safe;
		Grid.SetColumn(CertShareButton, safe ? 0 : 1);
		Grid.SetColumnSpan(CertShareButton, safe ? 2 : 1);

		CertRows.Children.Clear();
		var first = true;
		foreach (var row in status.Profiles)
		{
			if (!first)
				CertRows.Children.Add(new BoxView { Style = StyleOf("RowDivider") });
			first = false;

			var name = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center };
			name.Children.Add(new Label { Text = row.Profile.ProfileName, FontSize = 16 });
			if (row.Profile.NotAfterUtc is { } until)
				name.Children.Add(new Label
				{
					Text = string.Format(L10n.Get("lblCertBackupValidUntil"), until.ToLocalTime().ToString("d MMMM yyyy")),
					Style = StyleOf("RowHint"),
				});

			var chip = new Border
			{
				StrokeThickness = 0,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
				Padding = new Thickness(9, 4),
				VerticalOptions = LayoutOptions.Center,
				BackgroundColor = Token(row.IsBackedUp ? "OkSoft" : "WarnSoft"),
				Content = new Label
				{
					Text = row.BackedUpUtc is { } at
						? string.Format(L10n.Get("lblCertBackupBackedUp"), at.ToLocalTime().ToString("d MMM"))
						: L10n.Get("lblCertBackupNotBackedUp"),
					FontSize = 12,
					FontAttributes = FontAttributes.Bold,
				},
			};

			var grid = new Grid
			{
				ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
				ColumnSpacing = 12,
				Padding = new Thickness(16, 10),
				MinimumHeightRequest = 52,
			};
			grid.Add(name, 0);
			grid.Add(chip, 1);
			CertRows.Children.Add(grid);
		}
	}

	private async void OnBackUpNowClicked(object? sender, EventArgs e)
	{
		try
		{
			CertificateBackups.BackUpNow();
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblCertBackups"), string.Format(L10n.Get("lblCertBackupFailed"), ex.Message), L10n.Get("btn_Close"));
		}
		ShowCertificateBackups();
	}

	// The backup off the phone: a fresh archive of the same certificates into the cache, handed to the
	// share sheet (Drive, mail, another device). Downloads goes with a phone reset; this does not.
	private async void OnShareCertBackupClicked(object? sender, EventArgs e)
	{
		try
		{
			var name = CertificateBackups.FilePrefix + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + CertificateArchive.FileExtension;
			var path = Path.Combine(FileSystem.CacheDirectory, name);
			using (var stream = File.Create(path))
				CertificateArchive.ExportAll(stream, CertStorePath);
			await Share.Default.RequestAsync(new ShareFileRequest(L10n.Get("lblCertBackups"), new ShareFile(path)));
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblCertBackups"),
				string.Format(L10n.Get("statusCertificateExportFailed"), ErrorText.Describe(ex, "certificate/backup-share")),
				L10n.Get("btn_Close"));
		}
	}

	private async void OnViewLogClicked(object? sender, EventArgs e) => await Navigation.PushAsync(new LogViewerPage());

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
				Title = L10n.Get("lblDebugLog"),
				File = new ShareFile(logPath),
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblDebugLog"), string.Format(L10n.Get("statusLogShareFailed"), ex.Message), L10n.Get("btn_Close"));
		}
	}

	// TV-Store's own log from the TV the app is working with, saved next to the debug logs and handed
	// to the share sheet. TV-Store serves it to the LAN while it is open on the TV; no Developer Mode needed.
	private async void OnGetTvStoreLogClicked(object? sender, EventArgs e)
	{
		var tvIp = IPlatformApplication.Current?.Services.GetService<TvSelection>()?.Ip;
		if (string.IsNullOrWhiteSpace(tvIp))
		{
			ShowTvStoreLogStatus(L10n.Get("statusTvStoreLogNoTv"));
			return;
		}

		TvStoreLogButton.IsEnabled = false;
		ShowTvStoreLogStatus(string.Format(L10n.Get("statusTvStoreLogFetching"), tvIp));
		try
		{
			var folder = Path.GetDirectoryName(Apps2Samsung.Diagnostics.FileLog.CurrentLogFile) ?? Path.Combine(FileSystem.AppDataDirectory, "Logs");
			var path = await Apps2Samsung.TvStore.TvStoreLog.SaveAsync(tvIp, folder);
			ShowTvStoreLogStatus(string.Format(L10n.Get("statusTvStoreLogSaved"), Path.GetFileName(path)));
			await Share.Default.RequestAsync(new ShareFileRequest
			{
				Title = L10n.Get("lblGetTvStoreLog"),
				File = new ShareFile(path),
			});
		}
		catch (Apps2Samsung.TvStore.TvStoreLogUnavailableException ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] {ex.Message}");
			ShowTvStoreLogStatus(string.Format(L10n.Get("statusTvStoreLogFailed"), tvIp, ex.Reason));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[tvstore] getting TV-Store's log failed: {ex}");
			ShowTvStoreLogStatus(string.Format(L10n.Get("statusTvStoreLogFailed"), tvIp, ex.Message));
		}
		finally
		{
			TvStoreLogButton.IsEnabled = true;
		}
	}

	private void ShowTvStoreLogStatus(string text)
	{
		TvStoreLogStatus.Text = text;
		TvStoreLogStatus.IsVisible = !string.IsNullOrEmpty(text);
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
			["CertificatePreference"] = MobileSettings.CertificatePreference,
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
			["CameraPipConfigJson"] = MobileSettings.CameraPipConfigJson,
			["ImmiTvServerUrl"] = MobileSettings.ImmiTvServerUrl,
			["ImmiTvEmail"] = MobileSettings.ImmiTvEmail,
			["ImmiTvPassword"] = MobileSettings.ImmiTvPassword,
			["ImmiTvApiKey"] = MobileSettings.ImmiTvApiKey,
			["TizenTubeProxyOverride"] = MobileSettings.TizenTubeProxyOverride,
			["TizenTubeProxyHost"] = MobileSettings.TizenTubeProxyHost,
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

			await Share.Default.RequestAsync(new ShareFileRequest(L10n.Get("lblBackupFile"), new ShareFile(path)));
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
			var picked = await LocalFilePicker.PickAsync("application/zip", "application/octet-stream");
			if (picked is null)
				return;

			BackupImportResult import;
			using (var stream = File.OpenRead(picked.LocalPath))
				import = BackupService.Import(stream, CertStorePath);

			if (!string.IsNullOrEmpty(import.SettingsJson))
				await ApplySettingsJsonAsync(import.SettingsJson!);

			// Make the imported certificate the selected one.
			DefaultCertificateToImportedProfile();

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

	// ---- The certificate on its own (#921) ----

	private async void OnExportCertificateClicked(object? sender, EventArgs e)
	{
		try
		{
			var path = Path.Combine(FileSystem.CacheDirectory, CertificateArchive.SuggestedFileName);
			using (var stream = File.Create(path))
				CertificateArchive.Export(stream, CertStorePath);

			await Share.Default.RequestAsync(new ShareFileRequest(L10n.Get("lblCertificateArchive"), new ShareFile(path)));
		}
		catch (CertificateArchiveException)
		{
			await DisplayAlert(L10n.Get("certArchiveExportTitle"), L10n.Get("certArchiveNothingToExport"), L10n.Get("lblOk"));
		}
		catch (Exception ex)
		{
			await DisplayAlert(
				L10n.Get("certArchiveExportTitle"),
				string.Format(L10n.Get("statusCertificateExportFailed"), ErrorText.Describe(ex, "certificate/export")),
				L10n.Get("lblOk"));
		}
	}

	private async void OnImportCertificateClicked(object? sender, EventArgs e)
	{
		try
		{
			// The selected TV's DUID is only read during an install, so the preview here says nothing
			// about covering one; the certificate sheet during an install does.
			var outcome = await CertificateArchiveFiles.PickAndImportAsync(this, tvDuid: null);
			if (outcome is null)
				return;

			await DisplayAlert(
				L10n.Get("certArchiveImportTitle"),
				string.Format(L10n.Get("statusCertificateImported"), outcome.FileCount),
				L10n.Get("lblOk"));
		}
		catch (Exception ex)
		{
			await DisplayAlert(
				L10n.Get("certArchiveImportTitle"),
				string.Format(L10n.Get("statusCertificateImportFailed"), ErrorText.Describe(ex, "certificate/import")),
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
		// The three-way choice; older backups only carry PartnerSigning (read just above).
		if (GetString("CertificatePreference") is { } preference)
			MobileSettings.CertificatePreference = CertificatePreference.Normalize(preference);
		if (GetBool("PatchYoutubePlugin") is { } patchYt) MobileSettings.JellyfinPatchYoutube = patchYt;
		if (GetBool("TizenTubeProxyOverride") is { } ttProxy) MobileSettings.TizenTubeProxyOverride = ttProxy;

		if (GetString("ManualDuids") is { } duids) MobileSettings.ManualDuids = duids;
		if (GetString("JellyfinIP") is { } jfIp) MobileSettings.JellyfinServerUrl = jfIp;
		if (GetString("JellyfinUserId") is { } jfUser) MobileSettings.JellyfinUserId = jfUser;
		if (GetString("JellyfinServerId") is { } jfServer) MobileSettings.JellyfinServerId = jfServer;
		if (GetString("JellyfinServerName") is { } jfName) MobileSettings.JellyfinServerName = jfName;
		if (GetString("JellyfinServerLocalAddress") is { } jfLocal) MobileSettings.JellyfinServerLocalAddress = jfLocal;
		if (GetString("CustomCss") is { } css) MobileSettings.JellyfinCustomCss = css;
		if (GetString("TvAppChannelsJson") is { } channels) MobileSettings.TvAppChannelsJson = channels;
		if (GetString("CameraPipConfigJson") is { } cameraPip) MobileSettings.CameraPipConfigJson = cameraPip;
		if (GetString("ImmiTvServerUrl") is { } immiServer) MobileSettings.ImmiTvServerUrl = immiServer;
		if (GetString("ImmiTvEmail") is { } immiEmail) MobileSettings.ImmiTvEmail = immiEmail;
		if (GetString("TizenTubeProxyHost") is { } ttHost) MobileSettings.TizenTubeProxyHost = ttHost;
		if (GetString("CustomAppIconsJson") is { } icons) MobileSettings.CustomAppIconsJson = icons;

		// Secrets go through the async SecureStorage-backed setters.
		if (GetString("GitHubToken") is { } token) await MobileSettings.SetGitHubTokenAsync(token);
		if (GetString("JellyfinAccessToken") is { } accessToken) await MobileSettings.SetJellyfinAccessTokenAsync(accessToken);
		if (GetString("ImmiTvPassword") is { } immiPassword) await MobileSettings.SetImmiTvPasswordAsync(immiPassword);
		if (GetString("ImmiTvApiKey") is { } immiApiKey) await MobileSettings.SetImmiTvApiKeyAsync(immiApiKey);
	}

	// After an import, default the certificate picker to whichever level profile was restored — so the
	// imported certificate becomes the selected one (when exactly one level is present).
	private static void DefaultCertificateToImportedProfile()
	{
		var store = CertStorePath;
		bool Has(CertificatePrivilegeLevel level) =>
			CertificateProvisioningService.HasUsableAuthorCert(
				Path.Combine(store, CertificateProvisioningService.ProfileName(level)));

		MobileSettings.CertificatePreference = CertificatePreference.AfterImport(
			MobileSettings.CertificatePreference,
			hasPublic: Has(CertificatePrivilegeLevel.Public),
			hasPartner: Has(CertificatePrivilegeLevel.Partner));
	}
}
