using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Pages;
using Apps2Samsung.Models;
using Apps2Samsung.Update;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The in-app updater's entry point: checks GitHub for a newer build and shows the update sheet,
/// which downloads and hands the APK to Android's package installer. Run at startup from the
/// Install tab (quiet: nothing to say unless there is an update that wasn't skipped) and from
/// Settings (manual: every outcome is reported).
/// </summary>
public sealed class UpdateFlow
{
	private readonly GitHubUpdateChecker _checker;
	private bool _running;

	/// <summary>The last check that got an answer (quiet or manual); null until one did. The Settings
	/// hub's update card reads it.</summary>
	public UpdateCheckResult? LastResult { get; private set; }

	public UpdateFlow(GitHubUpdateChecker checker)
	{
		_checker = checker;
	}

	public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
	{
		var current = AppInfo.Current.VersionString;
		// Android sideloads the .apk; iOS reaches users through TestFlight / the App Store, so there
		// the sheet only points at the release.
		return await _checker.CheckForUpdateAsync(
			current,
			includePrereleases: MobileSettings.IncludeBetaUpdates,
			assetMatcher: OperatingSystem.IsAndroid()
				? name => name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
				: null,
			cancellationToken: ct);
	}

	public async Task RunAsync(Page host, bool manual)
	{
		if (_running)
			return;
		_running = true;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
			var result = await CheckAsync(cts.Token);
			if (result.IsSuccess)
				LastResult = result;

			if (!result.IsSuccess)
			{
				if (manual)
					await host.DisplayAlert(L10n.Get("UpdateCheckFailed"), result.ErrorMessage ?? string.Empty, L10n.Get("lblOk"));
				return;
			}
			if (!result.IsUpdateAvailable)
			{
				if (manual)
					await host.DisplayAlert(L10n.Get("UpdateCheckNow"), string.Format(L10n.Get("UpdateUpToDate"), result.CurrentVersion), L10n.Get("lblOk"));
				return;
			}
			if (!manual && result.LatestVersion == MobileSettings.SkippedUpdateVersion)
				return;

			await host.Navigation.PushModalAsync(new UpdateSheetPage(result));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[update] check failed: {ex.Message}");
			if (manual)
				await host.DisplayAlert(L10n.Get("UpdateCheckFailed"), ex.Message, L10n.Get("lblOk"));
		}
		finally
		{
			_running = false;
		}
	}
}
