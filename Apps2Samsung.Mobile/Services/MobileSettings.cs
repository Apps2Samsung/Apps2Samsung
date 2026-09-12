using Apps2Samsung.Packaging;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Mobile-native settings store. The desktop keeps a single settings.json god-object; on mobile
/// we persist only the install-relevant subset via <see cref="Preferences"/> (scalars/flags) and
/// <see cref="SecureStorage"/> (the GitHub PAT). Values are read/written eagerly, mirroring the
/// desktop's auto-save-on-change behavior.
/// </summary>
public static class MobileSettings
{
	private const string KeyLanguage = "language";
	private const string KeyRemoveOld = "remove_old_version";
	private const string KeyOpenAfter = "open_after_install";
	private const string KeyKeepWgt = "keep_wgt_file";
	private const string KeyShowAllJf = "show_all_jellyfin_versions";
	private const string KeyManualDuids = "manual_duids";
	private const string KeyTvAppChannels = "tvapp_channels_json";
	private const string KeyPartnerSigning = "partner_signing";
	private const string KeyForceLogin = "force_samsung_login";
	private const string KeyTryOverwrite = "try_overwrite";
	private const string KeyGitHubToken = "github_token"; // SecureStorage
	private const string KeyJellyfinServerUrl = "jellyfin_server_url";
	private const string KeyJellyfinUserId = "jellyfin_user_id";
	private const string KeyJellyfinServerId = "jellyfin_server_id";
	private const string KeyJellyfinServerName = "jellyfin_server_name";
	private const string KeyJellyfinServerLocalAddress = "jellyfin_server_local_address";
	private const string KeyJellyfinCustomCss = "jellyfin_custom_css";
	private const string KeyJellyfinPatchYoutube = "jellyfin_patch_youtube";
	private const string KeyJellyfinAccessToken = "jellyfin_access_token"; // SecureStorage
	private const string KeyRemoteTokenPrefix = "remote_token_"; // one per TV, keyed by the set's id (IP on older builds/sets)
	private const string KeyRemoteMacPrefix = "remote_mac_"; // one per TV, keyed by IP

	/// <summary>Uninstall the previous version before installing (desktop: "Remove old version").</summary>
	public static bool DeletePreviousInstall
	{
		get => Preferences.Get(KeyRemoveOld, false);
		set => Preferences.Set(KeyRemoveOld, value);
	}

	/// <summary>Launch the app on the TV after installing (desktop: "Open after installation").</summary>
	public static bool OpenAfterInstall
	{
		get => Preferences.Get(KeyOpenAfter, false);
		set => Preferences.Set(KeyOpenAfter, value);
	}

	/// <summary>Keep the downloaded .wgt instead of deleting it (desktop: "Preserve WGT file").</summary>
	public static bool KeepWgtFile
	{
		get => Preferences.Get(KeyKeepWgt, false);
		set => Preferences.Set(KeyKeepWgt, value);
	}

	/// <summary>List every Jellyfin release, not just the latest (desktop: "Show all Jellyfin versions").</summary>
	public static bool ShowAllJellyfinVersions
	{
		get => Preferences.Get(KeyShowAllJf, false);
		set => Preferences.Set(KeyShowAllJf, value);
	}

	/// <summary>Offer beta (pre-release) versions in the in-app update check, and download that beta
	/// rather than the older stable. On by default — the mobile app has historically shipped as -beta
	/// builds, so keep existing users on the beta channel unless they turn it off.</summary>
	public static bool IncludeBetaUpdates
	{
		get => Preferences.Get("include_beta_updates", true);
		set => Preferences.Set("include_beta_updates", value);
	}

	/// <summary>Force a fresh Samsung login + certificate even when a reusable one exists (desktop:
	/// "Force Samsung login"). Off by default.</summary>
	public static bool ForceSamsungLogin
	{
		get => Preferences.Get(KeyForceLogin, false);
		set => Preferences.Set(KeyForceLogin, value);
	}

	/// <summary>Attempt an overwrite-install and, on failure, retry after removing the old copy
	/// (desktop: "Override existing app"). On by default — matches the desktop's recovery behaviour.</summary>
	public static bool TryOverwrite
	{
		get => Preferences.Get(KeyTryOverwrite, true);
		set => Preferences.Set(KeyTryOverwrite, value);
	}

	/// <summary>
	/// Opt-in Partner-level distributor signing (experimental). Default false = Public. Only apps
	/// that use restricted privileges (e.g. vpnservice) need it; some TVs may reject partner-signed
	/// installs, and Samsung may not issue Partner certs for individual accounts.
	/// </summary>
	public static bool PartnerSigning
	{
		// Kept as a derived alias so IAppConfig/MobileAppConfig readers still work; the source of truth
		// is now CertificatePreference (a 3-way choice), like the desktop's certificate picker.
		get => CertificatePreference == CertificatePreferencePartner;
		set => CertificatePreference = value ? CertificatePreferencePartner : CertificatePreferenceAuto;
	}

	public const string CertificatePreferenceAuto = "Automatic";
	public const string CertificatePreferencePublic = "Public";
	public const string CertificatePreferencePartner = "Partner";
	private const string KeyCertPreference = "certificate_preference";

	/// <summary>Which signing certificate/level to use: "Automatic" (Public unless a package requires
	/// Partner), "Public", or "Partner" — mirrors the desktop's certificate picker. Migrates from the old
	/// <see cref="KeyPartnerSigning"/> flag on first read.</summary>
	public static string CertificatePreference
	{
		get => Preferences.Get(KeyCertPreference,
			Preferences.Get(KeyPartnerSigning, false) ? CertificatePreferencePartner : CertificatePreferenceAuto);
		set => Preferences.Set(KeyCertPreference, value ?? CertificatePreferenceAuto);
	}

	/// <summary>Extra TV DUIDs to pre-authorize in the signing cert (one per line/comma-separated).</summary>
	public static string ManualDuids
	{
		get => Preferences.Get(KeyManualDuids, string.Empty);
		set => Preferences.Set(KeyManualDuids, value ?? string.Empty);
	}

	// SecureStorage is async; cache secrets so callers on the request path can read them synchronously.
	private static string _gitHubToken = string.Empty;
	private static string _jellyfinAccessToken = string.Empty;

	/// <summary>The GitHub PAT (empty if unset). Backed by <see cref="SecureStorage"/>.</summary>
	public static string GitHubToken => _gitHubToken;

	/// <summary>Loads secure values into memory. Call once at startup before the catalog loads.</summary>
	public static async Task InitAsync()
	{
		try { _gitHubToken = await SecureStorage.GetAsync(KeyGitHubToken) ?? string.Empty; }
		catch { _gitHubToken = string.Empty; }

		try { _jellyfinAccessToken = await SecureStorage.GetAsync(KeyJellyfinAccessToken) ?? string.Empty; }
		catch { _jellyfinAccessToken = string.Empty; }
	}

	public static async Task SetGitHubTokenAsync(string? value)
	{
		_gitHubToken = value?.Trim() ?? string.Empty;
		try
		{
			if (string.IsNullOrEmpty(_gitHubToken))
				SecureStorage.Remove(KeyGitHubToken);
			else
				await SecureStorage.SetAsync(KeyGitHubToken, _gitHubToken);
		}
		catch { /* secure storage unavailable — keep the in-memory value for this run */ }
	}

	// ---- Jellyfin (Settings → Jellyfin): injected into a Jellyfin .wgt at install by the shared
	// JellyfinPackagePatcher via IAppConfig/MobileAppConfig. ----

	/// <summary>Full Jellyfin server URL as entered by the user (scheme://host:port[/base]).</summary>
	public static string JellyfinServerUrl
	{
		get => Preferences.Get(KeyJellyfinServerUrl, string.Empty);
		set => Preferences.Set(KeyJellyfinServerUrl, value ?? string.Empty);
	}

	/// <summary>Authenticated user id (from username/password login); paired with the access token.</summary>
	public static string JellyfinUserId
	{
		get => Preferences.Get(KeyJellyfinUserId, string.Empty);
		set => Preferences.Set(KeyJellyfinUserId, value ?? string.Empty);
	}

	/// <summary>Real server GUID from /System/Info/Public (prevents ServerMismatch on auto-login).</summary>
	public static string JellyfinServerId
	{
		get => Preferences.Get(KeyJellyfinServerId, string.Empty);
		set => Preferences.Set(KeyJellyfinServerId, value ?? string.Empty);
	}

	/// <summary>Human-readable server name shown in the Jellyfin server picker.</summary>
	public static string JellyfinServerName
	{
		get => Preferences.Get(KeyJellyfinServerName, string.Empty);
		set => Preferences.Set(KeyJellyfinServerName, value ?? string.Empty);
	}

	/// <summary>Server's self-reported LAN address, used as a reachable fallback in the server list.</summary>
	public static string JellyfinServerLocalAddress
	{
		get => Preferences.Get(KeyJellyfinServerLocalAddress, string.Empty);
		set => Preferences.Set(KeyJellyfinServerLocalAddress, value ?? string.Empty);
	}

	/// <summary>User custom CSS injected into index.html (empty = none).</summary>
	public static string JellyfinCustomCss
	{
		get => Preferences.Get(KeyJellyfinCustomCss, string.Empty);
		set => Preferences.Set(KeyJellyfinCustomCss, value ?? string.Empty);
	}

	/// <summary>Apply the YouTube-plugin ("error 153") fix to the package.</summary>
	public static bool JellyfinPatchYoutube
	{
		get => Preferences.Get(KeyJellyfinPatchYoutube, false);
		set => Preferences.Set(KeyJellyfinPatchYoutube, value);
	}

	/// <summary>Jellyfin access token (empty if unset). Backed by <see cref="SecureStorage"/>.</summary>
	public static string JellyfinAccessToken => _jellyfinAccessToken;

	public static async Task SetJellyfinAccessTokenAsync(string? value)
	{
		_jellyfinAccessToken = value?.Trim() ?? string.Empty;
		try
		{
			if (string.IsNullOrEmpty(_jellyfinAccessToken))
				SecureStorage.Remove(KeyJellyfinAccessToken);
			else
				await SecureStorage.SetAsync(KeyJellyfinAccessToken, _jellyfinAccessToken);
		}
		catch { /* secure storage unavailable — keep the in-memory value for this run */ }
	}

	/// <summary>Persisted TVApp channels as a JSON array of {name,url} (empty when unset).</summary>
	public static string TvAppChannelsJson
	{
		get => Preferences.Get(KeyTvAppChannels, string.Empty);
		set => Preferences.Set(KeyTvAppChannels, value ?? string.Empty);
	}

	/// <summary>The configured TVApp channels, injected into a TVApp wgt at install time.</summary>
	public static IReadOnlyList<TvChannel> GetTvAppChannels() =>
		TvAppChannelInjector.ParseChannelsJson(TvAppChannelsJson);

	/// <summary>Per-app custom launcher icons: JSON map { appKey -> "oblong" | custom PNG path },
	/// applied to the wgt at install by the shared CustomIconPackagePatcher. (Same Preferences key as
	/// MobileAppConfig.CustomAppIconsJson, so the settings page and the patcher share one store.)</summary>
	public static string CustomAppIconsJson
	{
		get => Preferences.Get("custom_app_icons_json", string.Empty);
		set => Preferences.Set("custom_app_icons_json", value ?? string.Empty);
	}

	/// <summary>Per-app custom launcher titles: JSON map { appKey -> title }, written into the wgt's
	/// config.xml &lt;name&gt; at install by the shared AppTitlePackagePatcher. (Same Preferences key as
	/// MobileAppConfig.CustomAppTitlesJson.)</summary>
	public static string CustomAppTitlesJson
	{
		get => Preferences.Get("custom_app_titles_json", string.Empty);
		set => Preferences.Set("custom_app_titles_json", value ?? string.Empty);
	}

	/// <summary>Splits <see cref="ManualDuids"/> into individual DUIDs.</summary>
	public static string[] ParseDuids() =>
		ManualDuids.Split(new[] { '\n', '\r', ',', ';' },
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// The UI language as a two-letter code. Empty until the first run has detected one, which is when
	/// the shared catalog commits the OS language (see L10n) — the same detect-once behaviour as the
	/// desktop head's AppSettings.Language.
	/// </summary>
	public static string Language
	{
		get => Preferences.Get(KeyLanguage, string.Empty);
		set => Preferences.Set(KeyLanguage, value);
	}

	/// <summary>
	/// The TV's remote-control pairing token, or null when this TV hasn't been paired yet. Kept in
	/// <see cref="Preferences"/> rather than SecureStorage: it only authorizes button presses to one
	/// TV on the local network, and it has to be readable synchronously while the remote page opens.
	/// </summary>
	public static string? GetRemoteToken(string tvKey)
	{
		var token = Preferences.Get(KeyRemoteTokenPrefix + tvKey, string.Empty);
		return string.IsNullOrEmpty(token) ? null : token;
	}

	/// <summary>Stores the token the TV handed back, so the next session connects without prompting.</summary>
	public static void SetRemoteToken(string tvKey, string token) =>
		Preferences.Set(KeyRemoteTokenPrefix + tvKey, token);

	/// <summary>
	/// The TV's MAC, remembered from a moment when the set was awake. A sleeping TV answers nothing,
	/// so this cache is what makes powering one back on possible at all (Wake-on-LAN).
	/// </summary>
	public static string? GetRemoteMac(string tvIpAddress)
	{
		var mac = Preferences.Get(KeyRemoteMacPrefix + tvIpAddress, string.Empty);
		return string.IsNullOrEmpty(mac) ? null : mac;
	}

	/// <summary>Remembers the TV's MAC for a later wake.</summary>
	public static void SetRemoteMac(string tvIpAddress, string macAddress) =>
		Preferences.Set(KeyRemoteMacPrefix + tvIpAddress, macAddress);
}

