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

		var sheet = new TvSheetPage(tv.Tvs, tv.SelectedIndex, tv.LocalIps());
		await page.Navigation.PushModalAsync(sheet);
		var choice = await sheet.Result;
		if (choice is null)
			return;
		tv.Select(choice.Index);

		// "Show me how" under a TV that needs attention (#1079): that TV is now the one in use, and
		// the Developer Mode guide opens for it.
		if (choice.Fix && DevModeGuidePage.IsNeeded(tv, tv.LocalIps()))
		{
			var guide = new DevModeGuidePage(tv, tv.LocalIps, fromInstall: false);
			await page.Navigation.PushModalAsync(guide);
			await guide.Completion;
		}
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
