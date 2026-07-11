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
	private const string KeyRemoveOld = "remove_old_version";
	private const string KeyOpenAfter = "open_after_install";
	private const string KeyKeepWgt = "keep_wgt_file";
	private const string KeyShowAllJf = "show_all_jellyfin_versions";
	private const string KeyManualDuids = "manual_duids";
	private const string KeyGitHubToken = "github_token"; // SecureStorage

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

	/// <summary>Extra TV DUIDs to pre-authorize in the signing cert (one per line/comma-separated).</summary>
	public static string ManualDuids
	{
		get => Preferences.Get(KeyManualDuids, string.Empty);
		set => Preferences.Set(KeyManualDuids, value ?? string.Empty);
	}

	// SecureStorage is async; cache the token so callers on the request path can read it synchronously.
	private static string _gitHubToken = string.Empty;

	/// <summary>The GitHub PAT (empty if unset). Backed by <see cref="SecureStorage"/>.</summary>
	public static string GitHubToken => _gitHubToken;

	/// <summary>Loads secure values into memory. Call once at startup before the catalog loads.</summary>
	public static async Task InitAsync()
	{
		try { _gitHubToken = await SecureStorage.GetAsync(KeyGitHubToken) ?? string.Empty; }
		catch { _gitHubToken = string.Empty; }
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

	/// <summary>Splits <see cref="ManualDuids"/> into individual DUIDs.</summary>
	public static string[] ParseDuids() =>
		ManualDuids.Split(new[] { '\n', '\r', ',', ';' },
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
