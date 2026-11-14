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

	/// <summary>Puts a name in the Install tab's search box (#1066); set by the Install tab once it is up.</summary>
	public static Action<string>? Search { get; set; }

	/// <summary>Opens an app's sheet on the Install tab; set by the Install tab once it is up.</summary>
	public static Func<CatalogOpenRequest, Task>? Open { get; set; }

	/// <summary>
	/// Update all (#1067): installs these, one after the other, on the Install tab (which owns the
	/// pipeline) and answers what happened to each; set by the Install tab once it is up.
	/// </summary>
	public static Func<IReadOnlyList<(GitHubRelease Release, Asset Asset)>, Task<IReadOnlyList<UpdateAllOutcome>>>? UpdateAll { get; set; }

	/// <summary>
	/// Restore a TV (#1069): the same run as <see cref="UpdateAll"/>, worded as a restore; set by the
	/// Install tab once it is up.
	/// </summary>
	public static Func<IReadOnlyList<(GitHubRelease Release, Asset Asset)>, Task<IReadOnlyList<UpdateAllOutcome>>>? Restore { get; set; }

	/// <summary>
	/// How many apps had an update on each TV when On the TV last read it (#1067), keyed by IP: the
	/// row under the TV card. Not a live check; it is as old as that read.
	/// </summary>
	public static Dictionary<string, int> UpdatesByTv { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Raised when <see cref="UpdatesByTv"/> changed.</summary>
	public static event Action? UpdatesChanged;

	public static void SetUpdates(string tvIp, int count)
	{
		UpdatesByTv[tvIp] = count;
		UpdatesChanged?.Invoke();
	}

	/// <summary>Apps the official catalog stopped distributing (#1033), to flag an installed copy.</summary>
	public static IReadOnlyList<CommunityCatalogRemoved> Removed { get; private set; } = Array.Empty<CommunityCatalogRemoved>();

	public static void Set(IReadOnlyList<GitHubRelease> releases, IReadOnlyList<CommunityCatalogRemoved>? removed = null)
	{
		Releases = releases;
		Removed = removed ?? Array.Empty<CommunityCatalogRemoved>();
	}

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
/// <see cref="Asset"/> is the file the sheet opens on: the variant the TV got (#975), when known.
/// <see cref="FromLink"/>: an install link opened it (#1066), and the sheet says so.
/// </summary>
public sealed record CatalogOpenRequest(GitHubRelease Release, string? InstalledVersion = null, bool IsUpdate = false, Asset? Asset = null, bool FromLink = false);

public enum UpdateAllState { Updated, Failed, Skipped, NotStarted }

/// <summary>What Update all did with one app (#1067). <paramref name="Detail"/>: the new version, or why not.</summary>
public sealed record UpdateAllOutcome(string Name, UpdateAllState State, string? Detail);
