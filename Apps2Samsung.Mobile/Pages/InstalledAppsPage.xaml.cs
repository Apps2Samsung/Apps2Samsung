using System;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// A quick overview of the apps installed on a TV (parsed from the shared <c>vd_applist</c> query via
/// <see cref="TizenInstalledApps"/>), with a per-app uninstall for user-removable apps. Read-only for
/// system apps.
/// </summary>
public partial class InstalledAppsPage : ContentPage
{
	private readonly ISdbEngine _sdb;
	private readonly string _tvIp;
	private readonly string _tvLabel;

	public InstalledAppsPage(ISdbEngine sdb, string tvIp, string tvLabel)
	{
		InitializeComponent();
		_sdb = sdb;
		_tvIp = tvIp;
		_tvLabel = tvLabel;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

	private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

	// A partial/failed install can leave a package dir that vd_applist never shows, so it can't be
	// removed from the list above. vd_appuninstall <packageId> still reclaims it, so offer a manual
	// escape hatch: type the package id and force-remove it.
	private async void OnRemoveLeftoverClicked(object? sender, EventArgs e)
	{
		var id = await DisplayPromptAsync(
			"Remove leftover",
			"Enter the package id of a leftover/partial install to remove:",
			"Remove", "Cancel", placeholder: "e.g. HarborTV");
		if (string.IsNullOrWhiteSpace(id))
			return;
		id = id.Trim();

		var confirm = await DisplayAlert(
			"Remove leftover",
			$"Force-remove package \"{id}\" from {_tvLabel}?", "Remove", "Cancel");
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
				await DisplayAlert("Remove failed",
					string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error, "OK");
				return;
			}
		}
		catch (Exception ex)
		{
			SetBusy(false);
			await DisplayAlert("Remove failed", ex.Message, "OK");
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
			var apps = TizenInstalledApps.Parse(result?.Output).ToList();
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
			AppsList.ItemsSource = apps;

			if (apps.Count == 0)
			{
				EmptyLabel.Text = "Couldn't read the app list from this TV.";
				CountLabel.Text = _tvLabel;
			}
			else
			{
				var removable = apps.Count(a => a.IsRemovable);
				var totalUsed = InstalledApp.FormatSize(apps.Sum(a => a.SizeBytes));
				CountLabel.Text = $"{apps.Count} apps · {totalUsed} used on {_tvLabel} · {removable} removable";
			}
		}
		catch (Exception ex)
		{
			AppsList.ItemsSource = null;
			EmptyLabel.Text = $"Couldn't read the app list: {ex.Message}";
			CountLabel.Text = _tvLabel;
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async void OnUninstallClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: InstalledApp app })
			return;

		var confirm = await DisplayAlert(
			"Uninstall app",
			$"Remove \"{app.DisplayName}\" from {_tvLabel}?\n\n({app.TizenId})",
			"Uninstall", "Cancel");
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
				await DisplayAlert("Uninstall failed",
					string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() : result.Error, "OK");
				return;
			}
		}
		catch (Exception ex)
		{
			SetBusy(false);
			await DisplayAlert("Uninstall failed", ex.Message, "OK");
			return;
		}

		// Refresh so the removed app drops off the list.
		await LoadAsync();
	}

	private async void OnLaunchClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: InstalledApp app })
			return;

		SetBusy(true, $"Launching {app.DisplayName}…");
		try
		{
			var result = await _sdb.LaunchAsync(_tvIp, app.TizenId);
			if (result.ExitCode != 0)
			{
				await DisplayAlert("Launch failed", result.Error, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Launch failed", ex.Message, "OK");
		}
		finally
		{
			SetBusy(false);
		}
	}

	private async void OnStopClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: InstalledApp app })
			return;

		SetBusy(true, $"Stopping {app.DisplayName}…");
		try
		{
			var result = await _sdb.ShellAsync(_tvIp, $"0 was_kill {app.TizenId}");
			if (result.ExitCode != 0)
			{
				await DisplayAlert("Stop failed", result.Error, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Stop failed", ex.Message, "OK");
		}
		finally
		{
			SetBusy(false);
		}
	}

	private void SetBusy(bool busy, string? status = null)
	{
		Busy.IsVisible = busy;
		Busy.IsRunning = busy;
		AppsList.IsVisible = !busy;
		if (status is not null)
			CountLabel.Text = status;
	}
}
