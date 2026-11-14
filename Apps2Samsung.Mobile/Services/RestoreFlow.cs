using Apps2Samsung.Catalog;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Restore a TV (#1069) across tabs: Settings › Backup picks a saved app list, the On the TV tab
/// owns the TV's own list and the sheet. One process, one request at a time, so a static holder
/// like <see cref="CatalogStore"/>.
/// </summary>
public static class RestoreFlow
{
	private static TvProfile? _pending;

	/// <summary>Raised when a list was handed over while the On the TV tab is already showing.</summary>
	public static event Action? Requested;

	/// <summary>Hands <paramref name="profile"/> to the On the TV tab and switches to it.</summary>
	public static async Task OpenOnTvTabAsync(TvProfile profile)
	{
		_pending = profile;
		await Shell.Current.GoToAsync("//apps");
		Requested?.Invoke();
	}

	/// <summary>The list waiting for the On the TV tab, taken once.</summary>
	public static TvProfile? Take()
	{
		var profile = _pending;
		_pending = null;
		return profile;
	}
}
