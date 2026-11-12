using System;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Controls;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// A quick overview of the apps installed on a TV (parsed from the shared <c>vd_applist</c> query via
/// <see cref="TizenInstalledApps"/>), with a per-app uninstall for user-removable apps. Read-only for
/// system apps.
/// </summary>
public partial class InstalledAppsPage : ContentPage
{
	private readonly ISdbEngine _sdb;
	private readonly TvSelection _tv;
	// The catalog's own icons (#1000), for the apps that came from the catalog.
	private readonly Apps2Samsung.Catalog.CatalogIcons _icons;
	// Installed app id → its catalog entry, for the row's Update button (#820).
	private readonly Dictionary<string, Apps2Samsung.Catalog.CatalogMatch> _matches = new(StringComparer.OrdinalIgnoreCase);
	// The TV this page is showing, read from the shared selection each time the tab appears.
	private string _tvIp = string.Empty;
	private string _tvLabel = string.Empty;
	private string? _loadedFor;

	public InstalledAppsPage(ISdbEngine sdb, TvSelection tv, Apps2Samsung.Catalog.CatalogIcons icons)
	{
		InitializeComponent();
		_sdb = sdb;
		_tv = tv;
		_icons = icons;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_tv.Changed -= OnTvChanged;
		_tv.Changed += OnTvChanged;
		RestoreFlow.Requested -= OnRestoreRequested;
		RestoreFlow.Requested += OnRestoreRequested;
		await ShowSelectedTvAsync(force: false);
		await OpenPendingRestoreAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_tv.Changed -= OnTvChanged;
		RestoreFlow.Requested -= OnRestoreRequested;
	}

	// Another TV picked with the ▾ (here or, while this tab was hidden, elsewhere): load its apps.
	private async void OnTvChanged()
	{
		if (_tv.Ip != _loadedFor || !_tv.HasTv)
			await ShowSelectedTvAsync(force: false);
	}

	private async Task ShowSelectedTvAsync(bool force)
	{
		TopBar.Title = _tv.HasTv ? _tv.DisplayName : L10n.Get("lblNoTvFound");
		TopBar.TvCount = _tv.Tvs.Count;
		if (!_tv.HasTv)
		{
			_tvIp = string.Empty;
			_tvLabel = string.Empty;
			_loadedFor = null;
			AppsList.ItemsSource = null;
			SetSubtitle(L10n.Get("statusSelectTvFirst"), TopBarStatus.Warn);
			return;
		}
		var changed = _tv.Ip != _loadedFor;
		_tvIp = _tv.Ip!;
		_tvLabel = _tv.Label;
		_loadedFor = _tv.Ip;
		if (force || changed || AppsList.ItemsSource is null)
			await LoadAsync();
	}

	/// <summary>
	/// The icon of every app that came from the catalog (#1000): the store-icon list only knows
	/// store apps, so Jellyfin, TizenTube and the community apps drew their letter. The catalog's
	/// icon wins where both exist. Fetched after the list is on screen (cached files show at once,
	/// a first download takes a moment); the list is shown again once they are in.
	/// </summary>
	private async Task ShowCatalogIconsAsync(List<InstalledApp> apps, string tvIp)
	{
		var wanted = apps
			.Select((app, index) => (index, match: _matches.TryGetValue(app.TizenId, out var m) ? m : null))
			.Where(x => x.match is not null)
			.ToList();
		if (wanted.Count == 0)
			return;
		var paths = await Task.WhenAll(wanted.Select(async x =>
		{
			try { return (x.index, path: await _icons.PathForAsync(x.match!.Release)); }
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine($"[installed] catalog icon for {x.match!.Release.Name}: {ex.Message}");
				return (x.index, path: (string?)null);
			}
		}));
		if (paths.All(p => p.path is null))
			return;
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			// Another TV, or another load, since: this list is stale.
			if (tvIp != _tvIp || AppsList.ItemsSource is null)
				return;
			foreach (var (index, path) in paths)
				if (path is not null)
					apps[index] = apps[index] with { IconUrl = path };
			AppsList.ItemsSource = InstalledAppSort.Sort(apps, MobileSettings.InstalledAppOrder).ToList();
		});
	}

	private void ShowRelayNotice(bool show)
	{
		RelayNotice.IsVisible = show;
		RelayNoticeLabel.Text = L10n.Get("lblOnTvRelayClosed");
		OpenTvStoreBtn.IsEnabled = true;
	}

	// The relay's own answer: listening, or TV-Store not answering at all (closed).
	private static async Task<bool> RelayListeningAsync(string tvIp)
	{
		try { return await Apps2Samsung.Sdb.SdbEndpoint.WaitForRelayAsync(tvIp, TimeSpan.Zero, untilReachable: true); }
		catch { return false; }
	}

	/// <summary>Opens TV-Store on the TV (the TV's own app launch, no SDB), waits for its relay, then loads the list.</summary>
	private async void OnOpenTvStoreClicked(object? sender, EventArgs e)
	{
		OpenTvStoreBtn.IsEnabled = false;
		var tvIp = _tvIp;
		var up = await Apps2Samsung.TvStore.TvStoreHandoff.OpenForRelayAsync(tvIp,
			key => MainThread.BeginInvokeOnMainThread(() => RelayNoticeLabel.Text = L10n.Get(key)));
		if (tvIp != _tvIp)
			return;
		if (!up)
		{
			RelayNoticeLabel.Text = L10n.Get("lblOnTvRelayNotUp");
			OpenTvStoreBtn.IsEnabled = true;
			return;
		}
		await LoadAsync();
	}

	private async void OnChangeTvClicked(object? sender, EventArgs e) => await TvPicker.ChooseAsync(this, _tv);

	// The ⋯ in the top bar: Refresh, TV information, and the escape hatch for a leftover package.
	private async void OnMoreClicked(object? sender, EventArgs e)
	{
		var choice = await MoreMenu.ShowAsync(this,
			new MoreMenuItem(L10n.Get("lblRefresh"), MoreMenu.IconRescan),
			new MoreMenuItem(L10n.Get("lblCheckForUpdates"), MoreMenu.IconRescan),
			new MoreMenuItem(string.Format(L10n.Get("lblSortBy"), OrderLabel(MobileSettings.InstalledAppOrder)), MoreMenu.IconSort),
			new MoreMenuItem(L10n.Get("lblTvInformation"), MoreMenu.IconInfo),
			// Restore a TV (#1069): this TV's saved list, also after "Not now".
			new MoreMenuItem(L10n.Get("lblRestoreApps"), MoreMenu.IconRescan),
			new MoreMenuItem(L10n.Get("lblRemoveLeftover"), MoreMenu.IconDelete, Destructive: true));
		if (choice == 4)
		{
			if (_saved is { Missing.Count: > 0 } saved)
				await RestoreAsync(saved.Profile);
			else
				await DisplayAlert(L10n.Get("lblRestoreApps"), L10n.Get("lblRestoreNoneMissing"), L10n.Get("lblOk"));
			return;
		}
		if (choice == 5)
			choice = 4;
		if (choice == 2)
		{
			await ChooseOrderAsync();
			return;
		}
		if (choice > 2)
			choice--;
		if (choice == 0)
			await ShowSelectedTvAsync(force: true);
		else if (choice == 1)
			await CheckForUpdatesAsync();
		else if (choice == 2)
		{
			if (_tv.HasTv)
				await Navigation.PushAsync(new DeviceInfoPage(_sdb, _tv.Ip!, _tv.Label, _tv.DebugPortOpen));
		}
		else if (choice == 3)
			await RemoveLeftoverAsync();
	}

	// A fresh catalog, then the TV's list again: the subtitle says how many apps can be updated (#820).
	private async Task CheckForUpdatesAsync()
	{
		if (CatalogStore.Refresh is { } refresh)
		{
			SetBusy(true, L10n.Get("lblCheckForUpdates"));
			try { await refresh(); }
			catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[apps] Catalog refresh failed: {ex.Message}"); }
		}
		await ShowSelectedTvAsync(force: true);
		if (AppsList.ItemsSource is List<InstalledApp> { Count: > 0 } apps && apps.Count(a => a.HasUpdate) == 0)
			SetSubtitle(L10n.Get("statusNoUpdates"), TopBarStatus.Ok);
	}

	private static readonly InstalledAppOrder[] Orders = { InstalledAppOrder.Size, InstalledAppOrder.Name, InstalledAppOrder.Newest };

	private static string OrderLabel(InstalledAppOrder order) => L10n.Get(order switch
	{
		InstalledAppOrder.Name => "lblSortName",
		InstalledAppOrder.Newest => "lblSortNewest",
		_ => "lblSortSize",
	});

	// Sort by (#888): size, name or install date, the current one switched on. Apps with an update
	// stay on top whichever is picked.
	private async Task ChooseOrderAsync()
	{
		var current = MobileSettings.InstalledAppOrder;
		var picked = await MoreMenu.ShowAsync(this,
			Orders.Select(o => new MoreMenuItem(OrderLabel(o), MoreMenu.IconSort, Checked: o == current)).ToArray());
		if (picked < 0 || Orders[picked] == current)
			return;
		MobileSettings.InstalledAppOrder = Orders[picked];
		if (AppsList.ItemsSource is List<InstalledApp> apps)
			AppsList.ItemsSource = InstalledAppSort.Sort(apps, Orders[picked]).ToList();
	}

	// "Update" on a row (#820): hand the catalog entry to the Install tab, which opens its sheet.
	private async void OnUpdateClicked(object? sender, EventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is InstalledApp app)
			await OpenInCatalogAsync(app);
	}

	// The app's sheet on the Install tab, with the TV's version on it and, when the catalog has
	// another, its button as Update (#888).
	private async Task OpenInCatalogAsync(InstalledApp app)
	{
		if (!_matches.TryGetValue(app.TizenId, out var match))
			return;
		await CatalogStore.OpenOnInstallTabAsync(new CatalogOpenRequest(match.Release, app.Version, match.HasUpdate, match.Asset));
	}

	private static string? CatalogLine(Apps2Samsung.Catalog.CatalogMatch match) => match.State switch
	{
		Apps2Samsung.Catalog.CatalogVersionState.UpdateAvailable => string.Format(L10n.Get("lblCatalogUpdateAvailable"), match.CatalogVersion),
		Apps2Samsung.Catalog.CatalogVersionState.Different => string.Format(L10n.Get("lblCatalogDifferentVersion"), match.CatalogVersion),
		Apps2Samsung.Catalog.CatalogVersionState.UpToDate => L10n.Get("lblCatalogUpToDate"),
		_ => null,
	};

	private async void OnPulledToRefresh(object? sender, EventArgs e)
	{
		try { await ShowSelectedTvAsync(force: true); }
		finally { Refresher.IsRefreshing = false; }
	}

	// A partial/failed install can leave a package dir that vd_applist never shows, so it can't be
	// removed from the list above. vd_appuninstall <packageId> still reclaims it, so offer a manual
	// escape hatch: type the package id and force-remove it.
	private async Task RemoveLeftoverAsync()
	{
		if (!_tv.HasTv)
			return;
		var id = await DisplayPromptAsync(
			"Remove leftover",
			"Enter the package id of a leftover/partial install to remove:",
			"Remove", "Cancel", placeholder: "e.g. HarborTV");
		if (string.IsNullOrWhiteSpace(id))
			return;
		id = id.Trim();

		var confirm = await DisplayAlert(
			L10n.Get("lblRemoveLeftoverTitle"),
			string.Format(L10n.Get("statusConfirmForceRemove"), id, _tvLabel),
			L10n.Get("lblRemove"), L10n.Get("lblCancel"));
		if (!confirm)
			return;

		SetBusy(true, $"Removing {id}…");
		try
		{
			var result = await _sdb.UninstallAsync(_tvIp, id);
			// Exit 0, or "failed[132]" (not installed / already gone) - both mean the leftover is cleared.
			var ok = result.ExitCode == 0 ||
					 (result.Output?.Contains("failed[132]", StringComparison.OrdinalIgnoreCase) ?? false);
			if (!ok)
			{
				SetBusy(false);
				await DisplayAlert(L10n.Get("lblRemoveFailed"),
					string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error, L10n.Get("lblOk"));
				return;
			}
		}
		catch (Exception ex)
		{
			SetBusy(false);
			await DisplayAlert(L10n.Get("lblRemoveFailed"), ex.Message, L10n.Get("lblOk"));
			return;
		}

		await LoadAsync();
	}

	private async Task LoadAsync()
	{
		SetBusy(true, L10n.Get("statusReadingInstalledApps"));
		try
		{
			ShowRelayNotice(false);
			var result = await _sdb.AppsAsync(_tvIp);
			// An empty list is the TV's answer (a fresh emulator, a reset set); a failed read throws.
			var apps = TizenInstalledApps.ParseOrThrow(result).ToList();
			var iconMap = await Apps2Samsung.Catalog.AppIconResolver.GetIconMapAsync();
			for (int i = 0; i < apps.Count; i++)
			{
				var a = apps[i];
				if ((!string.IsNullOrEmpty(a.AppId) && iconMap.TryGetValue(a.AppId, out var iconUrl)) ||
					iconMap.TryGetValue(a.TizenId, out iconUrl) ||
					iconMap.TryGetValue(a.DisplayName, out iconUrl) ||
					iconMap.TryGetValue(a.DisplayName.ToLowerInvariant(), out iconUrl))
				{
					apps[i] = a with { IconUrl = iconUrl };
				}
			}
			// The catalog entry each app came from, by the id its manifest declares (#820).
			_matches.Clear();
			var catalog = CatalogStore.Releases;
			for (int i = 0; i < apps.Count; i++)
			{
				// An app the catalog removed (#1033, direction C): a quiet line, the reason on its sheet,
				// and no catalog comparison, so no Update.
				if (Apps2Samsung.Catalog.RemovedApps.Find(apps[i], CatalogStore.Removed, catalog, MobileSettings.PackageIdRenamesJson) is { } gone)
				{
					apps[i] = apps[i] with { CatalogLine = L10n.Get(Apps2Samsung.Catalog.RemovedApps.RowTapKey), Removed = gone };
					continue;
				}
				// With the variant this TV got last time (#975), so an app with several files is
				// compared with the right one and Update opens the sheet on that one.
				if (Apps2Samsung.Catalog.InstalledAppUpdates.Find(apps[i], catalog, MobileSettings.PackageIdRenamesJson,
						MobileSettings.InstalledVariantsJson, _tvIp) is not { } match)
					continue;
				_matches[apps[i].TizenId] = match;
				apps[i] = apps[i] with { CatalogLine = CatalogLine(match), HasUpdate = match.HasUpdate };
			}
			AppsList.ItemsSource = InstalledAppSort.Sort(apps, MobileSettings.InstalledAppOrder).ToList();
			_ = ShowCatalogIconsAsync(apps, _tvIp);
			// The list as it is now, written down for this TV; apps gone since last time are offered again (#1069).
			SnapshotTv(apps);
			ShowMoved(Apps2Samsung.Catalog.Repositories.MovedApps.Hints(_matches.Values,
				Apps2Samsung.Catalog.UserSources.Parse(MobileSettings.UserSourcesJson)));

			var updates = apps.Count(a => a.HasUpdate);
			ShowUpdatesCard();
			if (apps.Count == 0)
			{
				EmptyLabel.Text = L10n.Get("statusNoInstalledApps");
				SetSubtitle(string.Empty, TopBarStatus.None);
			}
			else if (updates > 0)
				SetSubtitle(string.Format(L10n.Get("statusUpdatesAvailable"), updates), TopBarStatus.Warn);
			else
			{
				var totalUsed = InstalledApp.FormatSize(apps.Sum(a => a.SizeBytes));
				SetSubtitle(string.Format(L10n.Get("lblAppsOnTvCount"), apps.Count, totalUsed), TopBarStatus.Ok);
			}
		}
		catch (Exception ex)
		{
			AppsList.ItemsSource = null;
			_onTv = null;
			RestoreCard.IsVisible = false;
			EmptyLabel.Text = string.Format(L10n.Get("statusReadAppListFailed"), ex.Message);
			SetSubtitle(string.Empty, TopBarStatus.None);
			// Through TV-Store's relay (Host PC IP 127.0.0.1) a closed TV-Store is the usual reason.
			ShowRelayNotice(Apps2Samsung.Sdb.SdbEndpoint.UsesRelay(_tvIp) && !await RelayListeningAsync(_tvIp));
		}
		finally
		{
			SetBusy(false);
		}
	}

	// ---- apps that moved to their author's repository (#1018, the official catalog's moved_to) ----

	private static readonly System.Net.Http.HttpClient MovedHttp = new();

	/// <summary>One card per repository apps on this TV moved to: what moved, and Add repository.</summary>
	private void ShowMoved(System.Collections.Generic.IReadOnlyList<Apps2Samsung.Catalog.Repositories.MovedHint> hints)
	{
		MovedHost.Children.Clear();
		foreach (var hint in hints)
		{
			var add = new Button { Text = L10n.Get("lblMovedAdd"), Style = (Style)Application.Current!.Resources["PrimaryButton"] };
			add.Clicked += async (_, _) => await AddMovedAsync(hint, add);
			MovedHost.Children.Add(new Border
			{
				StrokeThickness = 0,
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
				Padding = new Thickness(14, 12),
				BackgroundColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("AccentSoft"),
				Content = new VerticalStackLayout
				{
					Spacing = 6,
					Children =
					{
						new Label { Text = L10n.Get("lblMovedTitle"), FontAttributes = FontAttributes.Bold, TextColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("Ink") },
						new Label { Text = string.Format(L10n.Get("hintMoved"), string.Join(", ", hint.AppNames), hint.Domain), FontSize = 15,
							LineBreakMode = LineBreakMode.WordWrap, TextColor = Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf("Ink") },
						add,
					},
				},
			});
		}
		MovedHost.IsVisible = hints.Count > 0;
	}

	/// <summary>Adds the repository with the key the official catalog named, then reloads the catalog and the list.</summary>
	private async Task AddMovedAsync(Apps2Samsung.Catalog.Repositories.MovedHint hint, Button button)
	{
		button.IsEnabled = false;
		try
		{
			var repositories = new Apps2Samsung.Catalog.Repositories.SignedRepositories(MovedHttp,
				System.IO.Path.Combine(FileSystem.AppDataDirectory, "repositories"));
			var (source, problem) = await Apps2Samsung.Catalog.Repositories.MovedApps.AdoptAsync(repositories, hint.To);
			if (source is null)
			{
				await DisplayAlert(L10n.Get("lblMovedTitle"), string.Format(L10n.Get("errMovedNotAdded"), problem), L10n.Get("btn_Close"));
				return;
			}
			var sources = Apps2Samsung.Catalog.UserSources.Parse(MobileSettings.UserSourcesJson);
			sources.Add(source);
			MobileSettings.UserSourcesJson = Apps2Samsung.Catalog.UserSources.Serialize(sources);
			if (CatalogStore.Refresh is { } refresh)
				await refresh();
			await LoadAsync();
		}
		finally
		{
			button.IsEnabled = true;
		}
	}

	private async Task UninstallAsync(InstalledApp app)
	{

		var confirm = await DisplayAlert(
			L10n.Get("lblUninstallApp"),
			string.Format(L10n.Get("statusConfirmUninstall"), app.DisplayName, _tvLabel, app.TizenId),
			L10n.Get("lblUninstall"), L10n.Get("lblCancel"));
		if (!confirm)
			return;

		SetBusy(true, $"Uninstalling {app.DisplayName}…");
		try
		{
			var result = await _sdb.UninstallAsync(_tvIp, app.TizenId);
			// The TV reports a not-installed code when the app is already gone - treat that as success.
			var ok = result.ExitCode == 0 ||
					 (result.Output?.Contains("failed[132]", StringComparison.OrdinalIgnoreCase) ?? false);
			if (!ok)
			{
				SetBusy(false);
				await DisplayAlert(L10n.Get("lblUninstallFailed"),
					string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error, L10n.Get("lblOk"));
				return;
			}
		}
		catch (Exception ex)
		{
			SetBusy(false);
			await DisplayAlert(L10n.Get("lblUninstallFailed"), ex.Message, L10n.Get("lblOk"));
			return;
		}

		// Removed on purpose: not an app to offer again after the next read (#1069).
		if (_matches.TryGetValue(app.TizenId, out var removedMatch))
			Apps2Samsung.Catalog.SavedTvs.RemoveApp(_tvIp, removedMatch.Release.Name, removedMatch.Release.Source.Key);
		// Refresh so the removed app drops off the list.
		await LoadAsync();
	}

	private async Task LaunchAsync(InstalledApp app)
	{

		SetBusy(true, $"Launching {app.DisplayName}…");
		try
		{
			var result = await _sdb.LaunchAsync(_tvIp, app.TizenId);
			if (result.ExitCode != 0)
			{
				await DisplayAlert(L10n.Get("lblLaunchFailed"), result.Error, L10n.Get("lblOk"));
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblLaunchFailed"), ex.Message, L10n.Get("lblOk"));
		}
		finally
		{
			SetBusy(false);
		}
	}

	// Opens the app's console over the TV's web inspector. Confirmed first because attaching is not
	// passive: the inspector only reports a port for the launch debug mode performs itself, so the app
	// restarts and whatever the user was watching goes away.
	private async Task DebugAsync(InstalledApp app)
	{

		var confirm = await DisplayAlert(
			L10n.Get("lblDebugConsole"),
			string.Format(L10n.Get("statusDebugConfirmRestart"), app.DisplayName),
			L10n.Get("lblOk"), L10n.Get("lblCancel"));
		if (!confirm)
			return;

		await Navigation.PushAsync(new DebugConsolePage(_sdb, _tvIp, app.TizenId, app.DisplayName));
	}

	private async Task StopAsync(InstalledApp app)
	{

		SetBusy(true, $"Stopping {app.DisplayName}…");
		try
		{
			var result = await _sdb.ShellAsync(_tvIp, $"0 was_kill {app.TizenId}");
			if (result.ExitCode != 0)
			{
				await DisplayAlert(L10n.Get("lblStopFailed"), result.Error, L10n.Get("lblOk"));
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblStopFailed"), ex.Message, L10n.Get("lblOk"));
		}
		finally
		{
			SetBusy(false);
		}
	}

	// A row opens the app's sheet; the choice comes back here.
	private async void OnRowTapped(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is not InstalledApp app)
			return;
		var sheet = new AppActionsSheetPage(app, _matches.ContainsKey(app.TizenId), app.Removed);
		await Navigation.PushModalAsync(sheet);
		switch (await sheet.Result)
		{
			case AppAction.Launch: await LaunchAsync(app); break;
			case AppAction.Stop: await StopAsync(app); break;
			case AppAction.Debug: await DebugAsync(app); break;
			case AppAction.Uninstall: await UninstallAsync(app); break;
			case AppAction.ViewInCatalog: await OpenInCatalogAsync(app); break;
		}
	}

	private void SetBusy(bool busy, string? status = null)
	{
		Busy.IsVisible = busy;
		Busy.IsRunning = busy;
		AppsList.IsVisible = !busy;
		if (busy && status is not null)
		{
			TopBar.Subtitle = status;
			TopBar.Status = TopBarStatus.Busy;
		}
		else if (!busy)
		{
			// The work is done: back to what the line said before it.
			TopBar.Subtitle = _subtitle;
			TopBar.Status = _subtitleStatus;
		}
	}

	// The line under the TV's name in the top bar: the app count and space, or what went wrong.
	private string _subtitle = string.Empty;
	private TopBarStatus _subtitleStatus;

	private void SetSubtitle(string text, TopBarStatus status)
	{
		_subtitle = text;
		_subtitleStatus = status;
		TopBar.Subtitle = text;
		TopBar.Status = status;
	}

	// ---- Update all (#1067) ----

	// The apps Update all takes: a newer catalog version, in list order. "Different" keeps its own Update.
	private List<(GitHubRelease Release, Asset Asset, string Name)> UpdateTargets() =>
		(AppsList.ItemsSource as IEnumerable<InstalledApp> ?? Enumerable.Empty<InstalledApp>())
			.Where(a => a.HasUpdate && _matches.TryGetValue(a.TizenId, out var m) && m.State == Apps2Samsung.Catalog.CatalogVersionState.UpdateAvailable)
			.Select(a => (_matches[a.TizenId].Release, _matches[a.TizenId].Asset, a.DisplayName))
			.ToList();

	private void ShowUpdatesCard()
	{
		var targets = UpdateTargets();
		if (!string.IsNullOrEmpty(_tvIp))
			CatalogStore.SetUpdates(_tvIp, targets.Count);
		UpdatesCard.IsVisible = targets.Count > 0 && CatalogStore.UpdateAll is not null;
		UpdatesTitle.Text = targets.Count == 1 ? L10n.Get("lblUpdateAllOne") : string.Format(L10n.Get("lblUpdateAllMany"), targets.Count);
		UpdatesNames.Text = string.Join(", ", targets.Select(t => t.Name));
	}

	// The installs run on the Install tab, which owns the pipeline and the progress sheet; back here
	// afterwards, with the list read again.
	private async void OnUpdateAllClicked(object? sender, EventArgs e)
	{
		if (CatalogStore.UpdateAll is not { } updateAll)
			return;
		var targets = UpdateTargets();
		if (targets.Count == 0)
			return;
		UpdatesCard.IsVisible = false;
		await Shell.Current.GoToAsync("//install");
		await updateAll(targets.Select(t => (t.Release, t.Asset)).ToList());
		await Shell.Current.GoToAsync("//apps");
		await LoadAsync();
	}

	// ---- Restore a TV (#1069) ----

	// What the TV listed at the last read (null while it could not be read), and what is kept for it.
	private List<InstalledApp>? _onTv;
	private Apps2Samsung.Catalog.SavedTv? _saved;

	// Nothing is written down without a catalog: every app would look like one the catalog does not have.
	private void SnapshotTv(List<InstalledApp> apps)
	{
		_onTv = apps;
		_saved = null;
		var catalog = CatalogStore.Releases;
		if (catalog.Count > 0)
		{
			try
			{
				var fresh = Apps2Samsung.Catalog.TvProfiles.Save(apps, catalog, CatalogStore.Removed, MobileSettings.PackageIdRenamesJson,
					MobileSettings.InstalledVariantsJson, _tvIp, null, null, includeSettings: false, DateTimeOffset.UtcNow, tvName: _tvLabel);
				_saved = Apps2Samsung.Catalog.SavedTvs.Remember(_tvIp, fresh.Profile, DateTimeOffset.UtcNow);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine($"[restore] Could not save the app list of {_tvIp}: {ex.Message}");
			}
		}
		ShowRestoreCard();
	}

	private void ShowRestoreCard()
	{
		var saved = _saved;
		var missing = saved is null
			? new List<Apps2Samsung.Catalog.TvProfileApp>()
			: saved.Profile.Apps.Where(a => saved.Missing.Contains(Apps2Samsung.Catalog.SavedTvs.AppKey(a))).ToList();
		RestoreCard.IsVisible = saved is { OfferRestore: true } && missing.Count > 0 && CatalogStore.Restore is not null;
		if (!RestoreCard.IsVisible)
			return;

		var date = saved!.Profile.SavedAtUtc.ToLocalTime().ToString("d MMMM");
		RestoreTitle.Text = missing.Count == 1 ? L10n.Get("lblRestoreMissingOne") : string.Format(L10n.Get("lblRestoreMissingMany"), missing.Count);
		RestoreText.Text = string.Format(L10n.Get("lblRestoreMissingText"), date, saved.Profile.Apps.Count);
		var names = missing.Take(4).Select(a => a.Name).ToList();
		RestoreNames.Text = missing.Count > names.Count
			? string.Format(L10n.Get("lblRestoreNamesMore"), string.Join(", ", names), missing.Count - names.Count)
			: string.Join(", ", names);
		RestoreButton.Text = missing.Count == 1 ? L10n.Get("lblRestoreAppsOne") : string.Format(L10n.Get("lblRestoreAppsMany"), missing.Count);
	}

	private void OnRestoreNotNowClicked(object? sender, EventArgs e)
	{
		Apps2Samsung.Catalog.SavedTvs.Dismiss(_tvIp);
		_saved = Apps2Samsung.Catalog.SavedTvs.Get(_tvIp);
		ShowRestoreCard();
	}

	private async void OnRestoreClicked(object? sender, EventArgs e)
	{
		if (_saved is { } saved)
			await RestoreAsync(saved.Profile);
	}

	// Settings › Backup handed over a saved list while this tab was already showing.
	private async void OnRestoreRequested() => await OpenPendingRestoreAsync();

	private async Task OpenPendingRestoreAsync()
	{
		if (RestoreFlow.Take() is { } profile)
			await RestoreAsync(profile);
	}

	/// <summary>The sheet for <paramref name="profile"/> against this TV, then the run on the Install tab, then the list again.</summary>
	private async Task RestoreAsync(Apps2Samsung.Catalog.TvProfile profile)
	{
		if (!_tv.HasTv || _onTv is null)
		{
			await DisplayAlert(L10n.Get("lblRestoreApps"), L10n.Get(_tv.HasTv ? "errRestoreNoList" : "statusSelectTvFirst"), L10n.Get("lblOk"));
			return;
		}
		var catalog = CatalogStore.Releases;
		if (catalog.Count == 0 || CatalogStore.Restore is not { } restore)
		{
			await DisplayAlert(L10n.Get("lblRestoreApps"), L10n.Get("errRestoreNoCatalog"), L10n.Get("lblOk"));
			return;
		}

		var plan = Apps2Samsung.Catalog.TvProfiles.Plan(profile, catalog, _onTv, MobileSettings.PackageIdRenamesJson, MobileSettings.InstalledVariantsJson, _tvIp);
		var sheet = new RestoreTvSheetPage(profile, plan, catalog, _tvLabel);
		await Navigation.PushModalAsync(sheet);
		if (await sheet.Result is not { Count: > 0 } picked)
			return;

		// Values a list file carries go where an install reads them, unless this phone has its own.
		foreach (var pick in picked)
		{
			if (pick.App.Settings is { Count: > 0 } carried && pick.Asset.UserSettings is { } spec &&
				Apps2Samsung.Packaging.UserSettingsStore.Get(MobileSettings.AppUserSettingsJson, pick.Asset.FileName) is null)
				MobileSettings.AppUserSettingsJson = Apps2Samsung.Packaging.UserSettingsStore.Set(MobileSettings.AppUserSettingsJson, pick.Asset.FileName, spec, carried);
		}

		RestoreCard.IsVisible = false;
		await Shell.Current.GoToAsync("//install");
		await restore(picked.Select(p => (p.Release, p.Asset)).ToList());
		await Shell.Current.GoToAsync("//apps");
		await LoadAsync();
	}
}
