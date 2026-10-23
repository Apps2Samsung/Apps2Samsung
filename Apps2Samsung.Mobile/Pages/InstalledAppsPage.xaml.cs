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
	// Installed app id → its catalog entry, for the row's Update button (#820).
	private readonly Dictionary<string, Apps2Samsung.Catalog.CatalogMatch> _matches = new(StringComparer.OrdinalIgnoreCase);
	// The TV this page is showing, read from the shared selection each time the tab appears.
	private string _tvIp = string.Empty;
	private string _tvLabel = string.Empty;
	private string? _loadedFor;

	public InstalledAppsPage(ISdbEngine sdb, TvSelection tv)
	{
		InitializeComponent();
		_sdb = sdb;
		_tv = tv;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		_tv.Changed -= OnTvChanged;
		_tv.Changed += OnTvChanged;
		await ShowSelectedTvAsync(force: false);
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_tv.Changed -= OnTvChanged;
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

	private async void OnChangeTvClicked(object? sender, EventArgs e) => await TvPicker.ChooseAsync(this, _tv);

	// The ⋯ in the top bar: Refresh, TV information, and the escape hatch for a leftover package.
	private async void OnMoreClicked(object? sender, EventArgs e)
	{
		var choice = await MoreMenu.ShowAsync(this,
			new MoreMenuItem(L10n.Get("lblRefresh"), MoreMenu.IconRescan),
			new MoreMenuItem(L10n.Get("lblCheckForUpdates"), MoreMenu.IconRescan),
			new MoreMenuItem(L10n.Get("lblTvInformation"), MoreMenu.IconInfo),
			new MoreMenuItem(L10n.Get("lblRemoveLeftover"), MoreMenu.IconDelete, Destructive: true));
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

	// "Update" on a row (#820): hand the catalog entry to the Install tab, which opens its sheet.
	private async void OnUpdateClicked(object? sender, EventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is not InstalledApp app || !_matches.TryGetValue(app.TizenId, out var match))
			return;
		CatalogStore.RequestOpen(match.Release);
		await Shell.Current.GoToAsync("//install");
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
			// Exit 0, or "failed[132]" (not installed / already gone) — both mean the leftover is cleared.
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
		SetBusy(true, "Reading installed apps…");
		try
		{
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
				if (Apps2Samsung.Catalog.InstalledAppUpdates.Find(apps[i], catalog) is not { } match)
					continue;
				_matches[apps[i].TizenId] = match;
				apps[i] = apps[i] with { CatalogLine = CatalogLine(match), HasUpdate = match.HasUpdate };
			}
			AppsList.ItemsSource = apps;

			var updates = apps.Count(a => a.HasUpdate);
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
			EmptyLabel.Text = $"Couldn't read the app list: {ex.Message}";
			SetSubtitle(string.Empty, TopBarStatus.None);
		}
		finally
		{
			SetBusy(false);
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
			// The TV reports a not-installed code when the app is already gone — treat that as success.
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
		var sheet = new AppActionsSheetPage(app);
		await Navigation.PushModalAsync(sheet);
		switch (await sheet.Result)
		{
			case AppAction.Launch: await LaunchAsync(app); break;
			case AppAction.Stop: await StopAsync(app); break;
			case AppAction.Debug: await DebugAsync(app); break;
			case AppAction.Uninstall: await UninstallAsync(app); break;
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
}
