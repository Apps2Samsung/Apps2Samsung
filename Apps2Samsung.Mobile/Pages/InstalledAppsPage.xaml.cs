using System;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Agent;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// A quick overview of the apps installed on a TV (parsed from the shared <c>vd_applist</c> query via
/// <see cref="TizenInstalledApps"/>), with a per-app uninstall for user-removable apps. Read-only for
/// system apps. Also the home of the install-leftovers card: sdbd's staging folder, where every
/// package ever pushed still sits, read and cleared by the debug agent — the same storage
/// housekeeping as the uninstalls, and like them SDB-only.
/// </summary>
public partial class InstalledAppsPage : ContentPage
{
	private readonly ISdbEngine _sdb;
	private readonly string _tvIp;
	private readonly string _tvLabel;

	// This head's installer, so the debug agent behind the leftovers card installs like any package
	// when the TV lacks it. Null where the caller has none; the agent then has to be on the set.
	private readonly Func<string, Action<string>, Task<bool>>? _installWgt;
	private DebugAgentClient? _agent;
	private bool _stagingBusy;

	public InstalledAppsPage(ISdbEngine sdb, string tvIp, string tvLabel, Func<string, Action<string>, Task<bool>>? installWgt = null)
	{
		InitializeComponent();
		_sdb = sdb;
		_tvIp = tvIp;
		_tvLabel = tvLabel;
		_installWgt = installWgt;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	protected override async void OnDisappearing()
	{
		base.OnDisappearing();
		await DetachAgentAsync();
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
	private async void OnDebugClicked(object? sender, EventArgs e)
	{
		if (sender is not Button { BindingContext: InstalledApp app })
			return;

		var confirm = await DisplayAlert(
			L10n.Get("lblDebugConsole"),
			string.Format(L10n.Get("statusDebugConfirmRestart"), app.DisplayName),
			L10n.Get("lblOk"), L10n.Get("lblCancel"));
		if (!confirm)
			return;

		await Navigation.PushAsync(new DebugConsolePage(_sdb, _tvIp, app.TizenId, app.DisplayName));
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

	private void SetBusy(bool busy, string? status = null)
	{
		Busy.IsVisible = busy;
		Busy.IsRunning = busy;
		AppsList.IsVisible = !busy;
		if (status is not null)
			CountLabel.Text = status;
	}

	// ---------------------------------------------------------------------------------------------
	// Install leftovers. sdbd pushes every package to /home/owner/share/tmp/sdk_tools and leaves it
	// there; its own "0 rmfile" verb deletes nothing on retail firmware. The debug agent runs on the
	// TV as that folder's owner, so it lists and clears the folder itself. Attached on the first tap
	// (installed first if the TV lacks it) and kept until the page closes.
	// ---------------------------------------------------------------------------------------------

	/// <summary>Reads the staging folder: attaches the agent if needed, then lists. Read-only.</summary>
	private async void OnCheckStagingClicked(object? sender, EventArgs e)
	{
		if (_stagingBusy)
			return;

		_stagingBusy = true;
		try
		{
			var agent = await AttachAgentAsync();
			if (agent is null)
				return;

			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingListing");
			var listing = await LoadStagedFilesAsync(agent);
			var packages = listing.Packages.ToList();
			StagingStatusLabel.Text = listing.Errors.Count > 0 && listing.Files.Count == 0
				? string.Format(L10n.Get("lblToolboxStagingFailed"), string.Join("; ", listing.Errors))
				: packages.Count == 0
					? L10n.Get("lblToolboxStagingEmpty")
					: string.Format(L10n.Get("lblToolboxStagingSummary"), packages.Count, InstalledApp.FormatSize(listing.PackageBytes));
		}
		catch (Exception ex)
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingFailed"), ex.Message);
		}
		finally
		{
			_stagingBusy = false;
		}
	}

	/// <summary>
	/// Deletes the package files in the staging folder — nothing else in there, and no installed app.
	/// On request only: nothing in the install flow calls this.
	/// </summary>
	private async void OnClearStagingClicked(object? sender, EventArgs e)
	{
		if (_stagingBusy)
			return;

		_stagingBusy = true;
		try
		{
			var agent = await AttachAgentAsync();
			if (agent is null)
				return;

			StagingStatusLabel.Text = L10n.Get("lblToolboxStagingClearing");
			var result = await agent.ClearStagingAsync();
			var status = result.Failed.Count == 0
				? string.Format(L10n.Get("lblToolboxStagingCleared"), result.Deleted.Count, InstalledApp.FormatSize(result.FreedBytes))
				: string.Format(L10n.Get("lblToolboxStagingPartial"), result.Deleted.Count, result.Failed.Count, string.Join("; ", result.Failed));

			// Show what is left, but keep the verdict: the clear is the news here, not the listing.
			try { await LoadStagedFilesAsync(agent); }
			catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installed-apps] staging re-list after clear: {ex.Message}"); }
			StagingStatusLabel.Text = status;
		}
		catch (Exception ex)
		{
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingFailed"), ex.Message);
		}
		finally
		{
			_stagingBusy = false;
		}
	}

	// The attached agent, attaching (and installing) on the first call. Null, with the reason in the
	// status line, when the agent could not be put on the TV or is too old for this.
	private async Task<DebugAgentClient?> AttachAgentAsync()
	{
		if (_agent is not null)
			return _agent;

		try
		{
			var progress = new Progress<string>(key => StagingStatusLabel.Text = L10n.Get(key));
			var agent = await DebugAgentClient.AttachCurrentAsync(
				_sdb, _tvIp, _installWgt,
				message => MainThread.BeginInvokeOnMainThread(() => StagingStatusLabel.Text = message),
				progress);
			agent.Disconnected += OnAgentDisconnected;
			_agent = agent;

			if (!agent.SupportsStaging)
			{
				StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxStagingAgentTooOld"), agent.AgentVersion, DebugAgentClient.StagingSince);
				await DetachAgentAsync();
				return null;
			}

			return agent;
		}
		catch (DebugAgentInstallException ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installed-apps] agent install: {ex.Message}");
			StagingStatusLabel.Text = L10n.Get(ex.Key);
			return null;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installed-apps] agent attach failed: {ex}");
			StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxAgentFailed"), ex.Message);
			return null;
		}
	}

	private async Task<DebugAgentStaging> LoadStagedFilesAsync(DebugAgentClient agent)
	{
		var listing = await agent.ListStagingAsync();
		var kept = L10n.Get("lblToolboxStagingKept");
		BindableLayout.SetItemsSource(StagedFileList, listing.Files.Select(f => new StagedFileRow(f, kept)).ToList());
		StagedFileScroll.IsVisible = true;
		ClearStagingBtn.IsEnabled = listing.Packages.Any();
		return listing;
	}

	private async Task DetachAgentAsync()
	{
		var agent = _agent;
		_agent = null;
		if (agent is not null)
		{
			agent.Disconnected -= OnAgentDisconnected;
			await agent.DisposeAsync();
		}
	}

	// Raised off the UI thread by the inspector's receive loop. The list stays; the next tap simply
	// attaches again.
	private void OnAgentDisconnected(string? reason) => MainThread.BeginInvokeOnMainThread(async () =>
	{
		if (_agent is null)
			return;
		await DetachAgentAsync();
		StagingStatusLabel.Text = string.Format(L10n.Get("lblToolboxAgentDisconnected"), reason ?? string.Empty);
	});
}

/// <summary>One file in the TV's install staging folder, as the agent listed it.</summary>
public sealed class StagedFileRow
{
	public StagedFileRow(DebugAgentStagedFile file, string keptLabel)
	{
		File = file;
		var detail = InstalledApp.FormatSize(file.Size);
		if (file.Modified is { } modified)
			detail += $" · {modified.LocalDateTime:g}";
		// A file a clear leaves alone (not a package) says so, so the list and the count agree.
		if (!file.IsPackage)
			detail += $" · {keptLabel}";
		Detail = detail;
	}

	public DebugAgentStagedFile File { get; }
	public string Name => File.Name;
	public string Detail { get; }
}
