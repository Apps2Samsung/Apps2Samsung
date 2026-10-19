using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Picking the TV, the same way from every tab: <see cref="ChooseAsync"/> is the TV pill in the top
/// bar (the Switch TV sheet over <see cref="TvSelection"/>), <see cref="AddByIpAsync"/> the ⋯ menu's
/// Add TV by IP and the no-TV card's button.
/// </summary>
public static class TvPicker
{
	public static async Task ChooseAsync(Page page, TvSelection tv)
	{
		// A running install keeps its TV; with one TV there is nothing to switch to.
		if (tv.IsLocked || tv.IsScanning || tv.Tvs.Count < 2)
			return;

		var sheet = new TvSheetPage(tv.Tvs, tv.SelectedIndex);
		await page.Navigation.PushModalAsync(sheet);
		var choice = await sheet.Result;
		if (choice is not null)
			tv.Select(choice.Index);
	}

	public static async Task AddByIpAsync(Page page, TvSelection tv)
	{
		if (tv.IsLocked || tv.IsScanning)
			return;
		var input = await page.DisplayPromptAsync(
			L10n.Get("lblManualTvIpTitle"),
			L10n.Get("lblManualTvIpPrompt"),
			accept: L10n.Get("lblAdd"), cancel: L10n.Get("lblCancel"), placeholder: "192.168.1.50");
		await tv.AddManualAsync(input);
	}
}
