using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The ▾ in a tab's top bar: opens the TV sheet over <see cref="TvSelection"/> and carries out the
/// choice (pick a TV, rescan, or type an IP). Every tab calls this, so they all pick the same way.
/// </summary>
public static class TvPicker
{
	public static async Task ChooseAsync(Page page, TvSelection tv)
	{
		// A running install keeps its TV.
		if (tv.IsLocked || tv.IsScanning)
			return;

		var sheet = new TvSheetPage(tv.Tvs, tv.SelectedIndex);
		await page.Navigation.PushModalAsync(sheet);
		var choice = await sheet.Result;
		if (choice is null)
			return;

		if (choice.Rescan)
			await tv.ScanAsync();
		else if (choice.Manual)
		{
			var input = await page.DisplayPromptAsync(
				L10n.Get("lblManualTvIpTitle"),
				L10n.Get("lblManualTvIpPrompt"),
				accept: L10n.Get("lblAdd"), cancel: L10n.Get("lblCancel"), placeholder: "192.168.1.50");
			await tv.AddManualAsync(input);
		}
		else
			tv.Select(choice.Index);
	}
}
