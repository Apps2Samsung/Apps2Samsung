using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// The catalog as the Install tab last loaded it, for the other tabs (#820): On the TV matches
/// installed apps against it and, for an Update, hands the entry back to the Install tab, which
/// owns the app sheet and the install pipeline. One process, one catalog, so a static holder.
/// </summary>
public static class CatalogStore
{
	public static IReadOnlyList<GitHubRelease> Releases { get; private set; } = Array.Empty<GitHubRelease>();

	/// <summary>Reloads the catalog from the network; set by the Install tab once it is up.</summary>
	public static Func<Task>? Refresh { get; set; }

	/// <summary>Opens an app's sheet on the Install tab; set by the Install tab once it is up.</summary>
	public static Func<CatalogOpenRequest, Task>? Open { get; set; }

	public static void Set(IReadOnlyList<GitHubRelease> releases) => Releases = releases;

	/// <summary>
	/// Switches to the Install tab and opens <paramref name="request"/>'s sheet there. The tab's
	/// own OnAppearing used to pick the request up, which lost it on Android (the sheet was pushed
	/// mid tab switch) and on an Install page rebuilt for a language change (#888), so the sheet is
	/// opened here, once the switch has finished.
	/// </summary>
	public static async Task OpenOnInstallTabAsync(CatalogOpenRequest request)
	{
		await Shell.Current.GoToAsync("//install");
		if (Open is { } open)
			await Shell.Current.Dispatcher.DispatchAsync(() => open(request));
	}
}

/// <summary>
/// An app sheet asked for from another tab (#820, #888). <see cref="InstalledVersion"/> is what the
/// TV runs, when it runs this app, and <see cref="IsUpdate"/> makes the sheet's button an Update.
/// </summary>
public sealed record CatalogOpenRequest(GitHubRelease Release, string? InstalledVersion = null, bool IsUpdate = false);
