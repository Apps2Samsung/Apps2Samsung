using System.Diagnostics;
using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Pages;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// What an install link opens on the phone (#1066): <c>apps2samsung://install?app=…</c> the app's
/// sheet on the Install tab, with the linked file selected and a line saying a link opened it;
/// <c>apps2samsung://source?url=…</c> Add a source, filled in. Nothing installs or is added without
/// the user's tap. The same rules as the desktop app, through Core's <see cref="InstallLink"/>.
/// </summary>
public static class InstallLinkHandler
{
	// One link at a time: a second one waits until the first sheet is closed.
	private static readonly SemaphoreSlim Gate = new(1, 1);

	public static async Task HandleAsync(string text, INavigation navigation, Page page)
	{
		await Gate.WaitAsync();
		try
		{
			await Shell.Current.GoToAsync("//install");
			if (!InstallLink.TryParse(text, out var link) || link is null)
			{
				Trace.WriteLine("[links] not an install link this version reads");
				await page.DisplayAlert(L10n.Get("lblLinkBrokenTitle"), L10n.Get("lblLinkBrokenBody"), L10n.Get("lblLinkClose"));
				return;
			}
			if (link.Kind == InstallLinkKind.Source)
				await AddSourceAsync(link.SourceUrl!, navigation);
			else
				await OpenAppAsync(link, page);
		}
		catch (Exception ex)
		{
			Trace.WriteLine($"[links] {ex}");
		}
		finally
		{
			Gate.Release();
		}
	}

	private static async Task AddSourceAsync(string url, INavigation navigation)
	{
		var sources = UserSources.Parse(MobileSettings.UserSourcesJson);
		var added = await UserSourceSheetPage.AskAsync(navigation, sources, url);
		if (added is null)
			return;
		sources.Add(added);
		MobileSettings.UserSourcesJson = UserSources.Serialize(sources);
		if (CatalogStore.Refresh is { } refresh)
			await refresh();
	}

	private static async Task OpenAppAsync(InstallLink link, Page page, bool retried = false)
	{
		// A cold start: the catalog is still coming in.
		var waited = Stopwatch.StartNew();
		while (CatalogStore.Releases.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(90))
			await Task.Delay(250);
		var all = CatalogStore.Releases;
		if (all.Count == 0)
		{
			await page.DisplayAlert(L10n.Get("lblLinkNoCatalogTitle"), L10n.Get("lblLinkNoCatalogBody"), L10n.Get("lblLinkClose"));
			return;
		}

		var visible = CommunityAppList.Visible(all, MobileSettings.ShowAdultApps, MobileSettings.ShowRootApps, MobileSettings.ShowClosedSourceApps).ToList();
		var match = InstallLink.Resolve(link, visible);
		if (match is null)
		{
			if (InstallLink.Resolve(link, all) is { } hidden)
			{
				// 18+ (#850) is a Settings choice: the button goes there, the user decides.
				if (hidden.Entries.All(e => e.IsAdult))
				{
					if (await page.DisplayAlert(L10n.Get("lblLinkAdultTitle"), string.Format(L10n.Get("lblLinkAdultBody"), hidden.Name),
							L10n.Get("lblOpenSettings"), L10n.Get("lblLinkClose")))
						await Shell.Current.GoToAsync("//settings");
					return;
				}
				// A root app, hidden while the catalog's ⋯ > Show root apps is off: the button turns it on and opens it.
				if (!retried && !MobileSettings.ShowRootApps
					&& await page.DisplayAlert(L10n.Get("lblLinkRootTitle"), string.Format(L10n.Get("lblLinkRootBody"), hidden.Name),
						L10n.Get("lblShowRootApps"), L10n.Get("lblLinkClose")))
				{
					MobileSettings.ShowRootApps = true;
					await OpenAppAsync(link, page, retried: true);
				}
				return;
			}
			if (await page.DisplayAlert(L10n.Get("lblLinkNotFoundTitle"), string.Format(L10n.Get("lblLinkNotFoundBody"), link.App),
					L10n.Get("lblSearchCatalog"), L10n.Get("lblLinkClose")))
				CatalogStore.Search?.Invoke(link.App!);
			return;
		}

		// Several sources offer it (#1018): never merged, the user picks one.
		var bySource = match.Entries.GroupBy(e => e.Source.Key).Select(g => g.First()).ToList();
		var release = bySource[0];
		if (bySource.Count > 1)
		{
			var labels = bySource.Select(r => $"{CatalogSourceLabels.Name(r.Source, L10n.Get)} · {Version(r)}").ToList();
			var picked = await page.DisplayActionSheet(string.Format(L10n.Get("lblLinkSourcesTitle"), match.Name),
				L10n.Get("lblCancel"), null, labels.ToArray());
			var index = picked is null ? -1 : labels.IndexOf(picked);
			if (index < 0)
				return;
			release = bySource[index];
		}
		var asset = match.File is null ? null
			: release.Assets.FirstOrDefault(a => string.Equals(a.FileName, match.File, StringComparison.OrdinalIgnoreCase));
		await CatalogStore.OpenOnInstallTabAsync(new CatalogOpenRequest(release, Asset: asset, FromLink: true));
	}

	private static string Version(GitHubRelease r) =>
		(r.Assets.FirstOrDefault(a => a.IsDefault) ?? r.Assets.FirstOrDefault())?.PackageVersion is { Length: > 0 } v ? $"v{v}" : r.TagName;
}
