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

	private static GitHubRelease? _pendingOpen;

	public static void Set(IReadOnlyList<GitHubRelease> releases) => Releases = releases;

	/// <summary>Asks the Install tab to open this app's sheet the next time it appears.</summary>
	public static void RequestOpen(GitHubRelease release) => _pendingOpen = release;

	public static GitHubRelease? TakePendingOpen()
	{
		var release = _pendingOpen;
		_pendingOpen = null;
		return release;
	}
}
