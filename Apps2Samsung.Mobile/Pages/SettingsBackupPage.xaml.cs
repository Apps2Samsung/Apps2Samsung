using System.Text.Json;
using System.Text.Json.Nodes;
using Apps2Samsung.Backup;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>Settings > Backup and diagnostics: settings + certificates out and in, and the debug log.</summary>
public partial class SettingsBackupPage : ContentPage
{
	public SettingsBackupPage()
	{
		InitializeComponent();
	}

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

		bool partner = Has(CertificatePrivilegeLevel.Partner);
		bool pub = Has(CertificatePrivilegeLevel.Public);

		if (partner && !pub)
			MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePartner;
		else if (pub && !partner)
			MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePublic;
		// If both or neither were imported, leave the preference as-is (Automatic covers "both").
	}
}
