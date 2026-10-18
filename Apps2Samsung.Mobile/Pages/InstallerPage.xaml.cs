using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Certificate;
using Apps2Samsung.Design;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Packaging;
using Apps2Samsung.Update;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public partial class InstallerPage : ContentPage
{
	private readonly INetworkService _networkService;
	private readonly ISamsungLoginService _loginService;
	private readonly CertificateProvisioner _certProvisioner;
	private readonly WgtInstaller _installer;
	private readonly CatalogService _catalog;
	private readonly UpdateFlow _updates;
	private readonly SessionState _session;
	private readonly ISdbEngine _sdb;
	// The TV the other tabs work with (On the TV, Remote): kept in step with the picker.
	private readonly TvSelection _tv;

	// Parallel lists backing the TV picker: IP + display label for each listed TV (discovered or
	// manually added). The picker also shows a trailing ManualIpLabel entry that isn't in these.
	private readonly List<string> _tvIps = new();
	private readonly List<string> _tvLabels = new();
	// Per-TV scan result, index-aligned with _tvIps (null for a hand-typed IP the TV's REST API didn't
	// answer for). Carries DebugPortOpen — false = detected via the REST API only (SDB debug port not
	// up yet), so it needs a restart before it can be installed to — plus the Developer-Mode fields
	// the shared pre-install guards check.
	private readonly List<NetworkDevice?> _tvDevices = new();
	// Index into the lists above of the TV in use; -1 = none. The TV sheet changes it.
	private int _selectedTv = -1;
	// Every catalog entry; the list shows the slice in the chosen category that matches the search.
	private IReadOnlyList<GitHubRelease> _allReleases = new List<GitHubRelease>();
	private string _query = string.Empty;
	// Categories the user expanded with "Show all".
	private readonly HashSet<string> _expanded = new();
	private bool _catalogLoading;
	private int _catalogFailed;
	// The provider manifest, for the preview images the app sheet shows; null until it loaded.
	private ProviderManifest? _manifest;

	private bool _initialized;
	private bool _uiReady;
	// What the TV card shows besides the TV itself: the last status line, and whether something
	// is running (scan, download, signing, install) so its LED pulses.
	private string _status = string.Empty;
	private bool _scanning;
	private bool _busy;

	public InstallerPage(INetworkService networkService, ISamsungLoginService loginService,
		CertificateProvisioner certProvisioner, WgtInstaller installer, CatalogService catalog,
		UpdateFlow updates, SessionState session, ISdbEngine sdb, TvSelection tv)
	{
		InitializeComponent();
		_tv = tv;
		_networkService = networkService;
		_loginService = loginService;
		_certProvisioner = certProvisioner;
		_installer = installer;
		_catalog = catalog;
		_updates = updates;
		_session = session;
		_sdb = sdb;

		// Shows the app version (ApplicationDisplayVersion), so it stays in sync with the build.
		VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_initialized)
			return;
		_initialized = true;

		await MobileSettings.InitAsync();
		var catalog = await LoadCatalogAsync();
		await ScanAsync();

		// A catalog problem is more actionable than the scan result, so let it have the last word.
		// (The "Custom WGT / TPK" entry is always available, so an empty catalog isn't a dead end.)
		if (catalog is null)
			SetStatus(L10n.Get("statusCatalogUnavailable"));
		else if (catalog.Releases.Count == 0)
			SetStatus(L10n.Get("statusCatalogEmpty"));
		else if (catalog.Failed > 0)
			SetStatus(string.Format(L10n.Get("statusCatalogPartial"), catalog.Failed, catalog.Total));

		// Quietly check for a newer build (best-effort; never blocks the UI).
		_ = CheckForUpdatesAsync();
	}

	// The in-app updater (UpdateFlow): quiet at startup, it only speaks up for a new build the
	// user hasn't skipped. Settings has the manual check.
	private async Task CheckForUpdatesAsync()
	{
		try { await _updates.RunAsync(this, manual: false); }
		catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installer] Update check failed: {ex.Message}"); }
	}

	private async Task<CatalogService.CatalogResult?> LoadCatalogAsync()
	{
		SetStatus(L10n.Get("lblLoadingAppList"));
		_catalogLoading = true;
		RebuildCatalog();
		CatalogService.CatalogResult? result = null;
		try
		{
			result = await _catalog.LoadReleasesAsync();
			_allReleases = result.Releases;
			_catalogFailed = result.Failed;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusCatalogFailed"), ex.Message));
			_allReleases = new List<GitHubRelease>();
		}
		finally
		{
			_catalogLoading = false;
		}

		RebuildCatalog();
		_uiReady = true;

		// The manifest only feeds the sheet's preview image; a failure costs nothing but the picture.
		try { _manifest = await _catalog.GetManifestAsync(); }
		catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installer] Manifest for previews failed: {ex.Message}"); }
		return result;
	}

	// The catalog as short groups per category (Core's CatalogGroups), each with "Show all" to
	// expand in place; a search is one flat result group. Three placeholder rows while loading.
	private void RebuildCatalog()
	{
		CatalogList.Clear();
		if (_catalogLoading)
		{
			var skeleton = new VerticalStackLayout();
			for (var i = 0; i < 3; i++)
			{
				if (i > 0)
					skeleton.Add(Hairline());
				skeleton.Add(new BoxView { HeightRequest = 66, Color = DesignTheme.ColorOf("Surface2"), Opacity = 0.7 });
			}
			CatalogList.Add(GroupBorder(skeleton));
			CatalogFoot.Text = string.Empty;
			return;
		}

		var groups = CatalogGroups.Build(_allReleases, _query, _expanded, L10n.Get);
		var any = false;
		foreach (var group in groups)
		{
			if (group.Rows.Count == 0)
				continue;
			any = true;
			CatalogList.Add(GroupTitle(group));
			var list = new VerticalStackLayout();
			var first = true;
			foreach (var release in group.Rows)
			{
				if (!first)
					list.Add(Hairline());
				first = false;
				list.Add(CatalogRow(release, SublineFor(release)));
			}
			CatalogList.Add(GroupBorder(list));
		}
		if (!any)
		{
			CatalogList.Add(GroupBorder(new Label
			{
				Text = L10n.Get(_allReleases.Count == 0 ? "statusCatalogEmpty" : "lblNoAppMatches"),
				TextColor = DesignTheme.ColorOf("Muted"), Padding = new Thickness(16, 14),
			}));
		}
		CatalogFoot.Text = _catalogFailed > 0
			? string.Format(L10n.Get("lblCatalogFootFailed"), _allReleases.Count, _catalogFailed)
			: string.Format(L10n.Get("lblCatalogFoot"), _allReleases.Count);
	}

	private static Border GroupBorder(View content) => new()
	{
		StrokeThickness = 0, BackgroundColor = DesignTheme.ColorOf("Surface"), Content = content,
		StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
	};

	// "Media  31" on the left, "Show all" / "Show less" on the right.
	private View GroupTitle(CatalogGroup group)
	{
		var title = new Label
		{
			Text = $"{group.Title}  {group.Count}", FontSize = 15, FontAttributes = FontAttributes.Bold,
			TextColor = DesignTheme.ColorOf("Muted"), VerticalOptions = LayoutOptions.Center,
		};
		var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Margin = new Thickness(4, 8, 4, 0) };
		grid.Add(title, 0, 0);
		if (group.CanToggle)
		{
			var toggle = new Label
			{
				Text = L10n.Get(group.IsExpanded ? "lblShowLess" : "lblShowAll"), FontSize = 14, FontAttributes = FontAttributes.Bold,
				TextColor = DesignTheme.ColorOf("Accent"), VerticalOptions = LayoutOptions.Center, Padding = new Thickness(8, 4),
			};
			var tap = new TapGestureRecognizer();
			tap.Tapped += (_, _) =>
			{
				if (!_expanded.Remove(group.CategoryId))
					_expanded.Add(group.CategoryId);
				RebuildCatalog();
			};
			toggle.GestureRecognizers.Add(tap);
			grid.Add(toggle, 1, 0);
		}
		return grid;
	}

	private string SublineFor(GitHubRelease release)
	{
		var category = AppCategories.DisplayName(release.Category, L10n.Get);
		return release.HasVariants
			? $"{category} · {string.Format(L10n.Get("lblVariantsCount"), release.Assets.Count)}"
			: category;
	}

	private static BoxView Hairline() => new() { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") };

	// One list row: the letter tile, name and subline, a chevron; tapping opens the app sheet.
	private View CatalogRow(GitHubRelease release, string subline)
	{
		var tile = new Border
		{
			WidthRequest = 44, HeightRequest = 44, StrokeThickness = 0,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			BackgroundColor = DesignTheme.ColorOf(AppTiles.ColorToken(release.Category, release.Name)),
			Content = new Label
			{
				Text = AppTiles.Letter(release.Name), FontSize = 20, FontAttributes = FontAttributes.Bold,
				TextColor = DesignTheme.ColorOf("OnAccent"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
			},
		};
		var text = new VerticalStackLayout
		{
			Spacing = 1, VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = release.Name, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation },
				new Label { Text = subline, FontSize = 13, TextColor = DesignTheme.ColorOf("Muted"), LineBreakMode = LineBreakMode.TailTruncation },
			},
		};
		var chevron = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M1,1 L7,6.5 L1,12")!,
			Stroke = new SolidColorBrush(DesignTheme.ColorOf("Ink")), StrokeThickness = 2, Opacity = 0.35,
			WidthRequest = 8, HeightRequest = 13, VerticalOptions = LayoutOptions.Center,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(44), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 52,
		};
		grid.Add(tile, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(chevron, 2, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += async (_, _) => await OpenAppAsync(release, subline);
		grid.GestureRecognizers.Add(tap);
		return grid;
	}

	private void OnSearchChanged(object? sender, TextChangedEventArgs e)
	{
		_query = (e.NewTextValue ?? string.Empty).Trim();
		RebuildCatalog();
	}

	// The app sheet: preview, description, the version or variant to install, Install for the
	// selected TV. Hands back the asset, or null when closed.
	private async Task OpenAppAsync(GitHubRelease release, string subline)
	{
		if (_busy)
			return;
		var idx = _selectedTv;
		var device = idx >= 0 && idx < _tvDevices.Count ? _tvDevices[idx] : null;
		var ip = idx >= 0 && idx < _tvIps.Count ? _tvIps[idx] : null;
		var tvName = ip is null ? null : string.IsNullOrWhiteSpace(device?.DeviceName) ? ip : device!.DeviceName;
		var tvReady = ip is not null && (device?.DebugPortOpen ?? true);

		var sheet = new AppSheetPage(release, CatalogPreviews.For(_manifest, release), subline, tvName, tvReady);
		await Navigation.PushModalAsync(sheet);
		var asset = await sheet.Result;
		if (asset is not null)
			await InstallAsync(release, asset, null);
	}

	// "Your own package": a .wgt or .tpk from the phone, then the same install as a catalog app.
	private async void OnOwnFileTapped(object? sender, TappedEventArgs e)
	{
		if (_busy)
			return;
		var path = await PickCustomWgtAsync();
		if (path is not null)
			await InstallAsync(null, null, path);
	}

	// Lets the user pick a local .wgt or .tpk (the desktop takes both); copies it into the cache so re-signing/cleanup never touches
	// their original file. Returns the cache copy's path, or null when nothing valid was picked.
	private async Task<string?> PickCustomWgtAsync()
	{
		try
		{
			var picked = await LocalFilePicker.PickAsync();
			if (picked is null)
			{
				SetStatus(L10n.Get("statusNoFileSelected"));
				return null;
			}
			if (!picked.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) &&
				!picked.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase))
			{
				SetStatus(L10n.Get("statusChooseWgt"));
				return null;
			}

			var dest = Path.Combine(FileSystem.CacheDirectory, picked.FileName);
			File.Copy(picked.LocalPath, dest, overwrite: true);
			SetStatus(string.Format(L10n.Get("statusCustomPackageReady"), picked.FileName));
			return dest;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusFileUnreadable"), ex.Message));
			return null;
		}
	}

	// Makes the TV at index the one in use (−1 = none): the card, the other tabs and the status
	// line follow.
	private void SelectTv(int index)
	{
		_selectedTv = index >= 0 && index < _tvIps.Count ? index : -1;
		PublishTvSelection();
		UpdateTvCard();
	}

	// The "Change TV" chip: the found TVs, "enter an IP", or rescan.
	private async void OnChangeTvClicked(object? sender, EventArgs e)
	{
		if (_busy)
			return;
		var sheet = new TvSheetPage(_tvIps, _tvLabels, _tvDevices, _selectedTv);
		await Navigation.PushModalAsync(sheet);
		var choice = await sheet.Result;
		if (choice is null)
			return;
		if (choice.Rescan)
			await ScanAsync();
		else if (choice.Manual)
			await PromptManualIpAsync();
		else
		{
			SelectTv(choice.Index);
			SetStatus(SelectedTvStatus());
		}
	}

	// The TV the other tabs read (On the TV, Remote).
	private void PublishTvSelection()
	{
		var idx = _selectedTv;
		if (idx < 0 || idx >= _tvIps.Count)
		{
			_tv.Clear();
			return;
		}
		_tv.Set(_tvIps[idx], _tvLabels[idx], idx < _tvDevices.Count ? _tvDevices[idx] : null);
	}

	// Prompts for a TV IP the scan didn't discover, validates it, and adds it as a selectable entry.
	private async Task PromptManualIpAsync()
	{
		var input = await DisplayPromptAsync(
			L10n.Get("lblManualTvIpTitle"),
			L10n.Get("lblManualTvIpPrompt"),
			accept: L10n.Get("lblAdd"), cancel: L10n.Get("lblCancel"), placeholder: "192.168.1.50");

		if (string.IsNullOrWhiteSpace(input) || !System.Net.IPAddress.TryParse(input.Trim(), out _))
		{
			if (!string.IsNullOrWhiteSpace(input))
				SetStatus(L10n.Get("statusInvalidIp"));
			return;
		}

		var ip = input.Trim();
		var idx = _tvIps.IndexOf(ip);
		if (idx < 0)
		{
			// Ask the TV's REST API for its Developer-Mode state so a hand-typed IP gets the same
			// pre-install guards as a scanned one. Nothing is lost when it doesn't answer: the entry
			// is still added (assumed ready, a real SDB error surfaces at install) and stays unguarded.
			var probed = await ProbeTvAsync(ip);
			_tvIps.Add(ip);
			_tvLabels.Add(string.Format(L10n.Get("lblTvManualEntry"), ip));
			_tvDevices.Add(probed);
			idx = _tvIps.Count - 1;
		}
		SelectTv(idx);
		SetStatus(string.Format(L10n.Get("statusUsingTvAt"), ip));
	}

	private async void OnShowDeviceInfoClicked(object? sender, EventArgs e)
	{
		if (_selectedTv < 0 || _selectedTv >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var idx = _selectedTv;
		var tvIp = _tvIps[idx];
		var label = _tvLabels[_selectedTv];
		var debugPortOpen = idx >= _tvDevices.Count || (_tvDevices[idx]?.DebugPortOpen ?? true);
		await Navigation.PushAsync(new DeviceInfoPage(_sdb, tvIp, label, debugPortOpen));
	}

	// Same story as the remote: the toolbox rides the network channel, so it is offered for any TV
	// on the network, Developer Mode or not.
	private async void OnShowTvToolboxClicked(object? sender, EventArgs e)
	{
		if (_selectedTv < 0 || _selectedTv >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var tvIp = _tvIps[_selectedTv];
		var label = _tvLabels[_selectedTv];

		// The toolbox's debug agent is a .wgt like any other: the normal pipeline (certificate, resign,
		// push) puts it on the TV when the set doesn't have it yet (#34).
		Func<string, Action<string>, Task<bool>> installWgt = async (wgtPath, report) =>
		{
			var cert = await _certProvisioner.ProvisionAsync(tvIp, false, report);
			await _installer.InstallAsync(tvIp, wgtPath, cert, report);
			return true;
		};

		await Navigation.PushAsync(new TvToolboxPage(_sdb, tvIp, label, installWgt));
	}

	private async void OnRefreshClicked(object? sender, EventArgs e) => await ScanAsync();

	private async Task ScanAsync()
	{
		RefreshBtn.IsEnabled = false;
		_scanning = true;
		_busy = true;
		SetStatus(L10n.Get("statusScanningForTvs"));
		try
		{
			// Keep not-ready TVs (REST API answered but the SDB debug port isn't up): they're shown
			// with a ⚠ marker so the user knows the TV was found but needs a restart first, instead of
			// silently disappearing from the list. The shared scan also enriches every TV with its
			// Developer-Mode state (name, developerMode, developerIP) — what the guards below and the
			// not-ready hint need to tell the user what to fix.
			var devices = await TizenDeveloperInfo.ScanAsync(_networkService);

			var selected = _selectedTv >= 0 && _selectedTv < _tvIps.Count ? _tvIps[_selectedTv] : null;

			_tvIps.Clear();
			_tvLabels.Clear();
			_tvDevices.Clear();
			foreach (var d in devices)
			{
				_tvIps.Add(d.IpAddress);
				_tvLabels.Add(d.DisplayText);
				_tvDevices.Add(d);
			}

			var keep = selected is null ? -1 : _tvIps.IndexOf(selected);
			SelectTv(_tvIps.Count > 0 ? (keep >= 0 ? keep : 0) : -1);

			SetStatus(_tvIps.Count == 0
				? L10n.Get("statusNoTvsFoundHint")
				: SelectedTvStatus());
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusScanFailed"), ex.Message));
		}
		finally
		{
			_scanning = false;
			_busy = false;
			RefreshBtn.IsEnabled = true;
			UpdateTvCard();
		}
	}

	// Downloads (or takes the picked file), provisions the certificate, installs on the selected
	// TV. Status goes to the TV card; its LED pulses for the duration.
	private async Task InstallAsync(GitHubRelease? release, Asset? asset, string? customPath)
	{
		if (_selectedTv < 0 || _selectedTv >= _tvIps.Count)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var custom = customPath is not null;
		if (!custom && (release is null || asset is null))
		{
			SetStatus(L10n.Get("statusSelectAppFirst"));
			return;
		}

		var tvIp = _tvIps[_selectedTv];

		// The device list is a scan snapshot; re-read the TV's Developer-Mode fields now so a
		// Developer-Mode IP corrected on the TV since the scan isn't still flagged as a mismatch.
		await TizenDeveloperInfo.RefreshAsync(_networkService, _tvDevices[_selectedTv]);

		// Shared pre-install guards (Core): a TV that still needs a restart, Developer Mode off, a
		// Developer-Mode IP pointing at another device or typed back to front, a TV on another subnet.
		// Same checks and wording as the desktop head — each one is Continue/Stop, never a hard block.
		var guardResult = InstallGuards.Evaluate(
			_tvDevices[_selectedTv],
			new InstallGuardOptions
			{
				LocalIps = LocalIps(),
				ConfiguredLocalIp = NetworkInfo.GetLocalIPv4(),
			},
			_networkService);

		foreach (var guard in guardResult.Guards)
		{
			bool continueAnyway = await DisplayAlert(
				guard.DefaultTitle, guard.DefaultMessageWithDetail,
				L10n.Get("keyContinue"), L10n.Get("keyStop"));
			if (!continueAnyway)
				return;
		}

		tvIp = guardResult.CorrectedTvIp ?? tvIp;

		var appName = custom ? Path.GetFileNameWithoutExtension(customPath!) : release!.Name;
		// Auto-request Partner signing if the selected app's manifest declares cert_level: partner.
		var requirePartner = !custom && release!.RequiresPartner;

		RefreshBtn.IsEnabled = false;
		_busy = true;
		UpdateTvCard();
		string? wgtPath = null;
		try
		{
			// Download first so we can inspect the package's declared privileges before signing.
			wgtPath = custom ? customPath! : await _installer.DownloadAsync(asset!.DownloadUrl, SetStatus);

			// Partner if the manifest declared it OR the .wgt itself needs a partner-level privilege.
			var needsPartner = requirePartner || WgtPrivileges.RequiresPartner(wgtPath);
			// Provisioning reuses a valid cert already covering this TV and only triggers a Samsung
			// sign-in when it must (re)generate — so no login prompt when a cert is already in place.
			var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);

			// The signing certificate's validity period may not have started yet — a Samsung TV
			// rejects such a package ("Certificate in signature is not valid yet"). Hold the install
			// behind a countdown to the moment it becomes valid instead of pushing something the TV
			// will refuse; the loop re-runs the check once the user chooses to continue.
			while (true)
			{
				try
				{
					await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus);
					break;
				}
				catch (CertificateNotYetValidException notYetValid)
				{
					SetStatus(L10n.Get("certificateWaiting"));
					var wait = new CertificateWaitPage(notYetValid.Result);
					await Navigation.PushModalAsync(wait);
					if (!await wait.Completion)
					{
						SetStatus(L10n.Get("certificateWaitCancelled"));
						return;
					}
				}
			}

			SetStatus("✓ " + string.Format(L10n.Get("statusInstalledOpenApps"), appName));
			await Navigation.PushModalAsync(new InstallCompletePage(appName));
		}
		catch (TaskCanceledException)
		{
			SetStatus(L10n.Get("statusSignInCancelled"));
		}
		catch (SamsungAccountEmailMissingException)
		{
			// The Samsung account has no email, so the distributor CSR has nothing to put in its
			// emailAddress field — this used to surface as a bare NullReferenceException (issue #606).
			// Only the user can fix it, on their Samsung account page, so offer to open it.
			SetStatus(L10n.Get("statusSamsungAccountNoEmail"));
			var openAccount = await DisplayAlert(
				L10n.Get("lblSamsungAccountNoEmailTitle"),
				L10n.Get("statusSamsungAccountNoEmail"),
				L10n.Get("lblOpenSamsungAccount"),
				L10n.Get("btn_Close"));

			if (openAccount)
			{
				try { await Launcher.Default.OpenAsync(SamsungAccountEmailMissingException.AccountUrl); }
				catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installer] Couldn't open the Samsung account page: {ex.Message}"); }
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Install failed: {ex}");
			SetStatus(string.Format(L10n.Get("statusInstallFailed"), ex.Message));
		}
		finally
		{
			// Clean up the staged package unless the user opted to keep it. For a custom install
			// this is the cache copy, never the user's original file.
			if (wgtPath is not null && !MobileSettings.KeepWgtFile)
			{
				try { File.Delete(wgtPath); } catch { /* best-effort */ }
			}

			RefreshBtn.IsEnabled = true;
			_busy = false;
			UpdateTvCard();
		}
	}

	// The IPs this phone answers on — what the guards compare the TV's Developer-Mode IP against.
	// On Android interface enumeration classifies nothing, so this resolves to the socket-derived
	// phone IP the scanner is configured with (empty when offline: the guards then skip the IP
	// checks rather than guess).
	private List<string> LocalIps()
	{
		try
		{
			return _networkService.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Couldn't enumerate local IPs: {ex.Message}");
			return new List<string>();
		}
	}

	// Reads a TV's Developer-Mode state over its REST API; null when it doesn't answer.
	private async Task<NetworkDevice?> ProbeTvAsync(string ip)
	{
		try
		{
			var enriched = await TizenDeveloperInfo.EnrichAsync(
				_networkService, new[] { new NetworkDevice { IpAddress = ip } });
			return enriched.FirstOrDefault();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Couldn't read TV info for {ip}: {ex.Message}");
			return null;
		}
	}

	// Status line for the current selection. A TV that isn't installable yet gets the actual reason
	// (Developer Mode off, its Developer-Mode IP pointing at another device, or a power cycle needed)
	// instead of a bare count — the same guidance the desktop head shows.
	private string SelectedTvStatus()
	{
		var idx = _selectedTv;
		var device = idx >= 0 && idx < _tvDevices.Count ? _tvDevices[idx] : null;
		var localIp = NetworkInfo.GetLocalIPv4();

		if (device is { DebugPortOpen: false })
			return TizenDeviceReadiness.Describe(TizenDeviceReadiness.WhyNotReady(device, localIp), localIp, L10n.Get);

		var notReady = _tvDevices.Count(d => d is { DebugPortOpen: false });
		return notReady > 0
			? string.Format(L10n.Get("statusTvsFoundNotReady"), _tvIps.Count, notReady)
			: L10n.Get("Ready");
	}

	private void SetStatus(string message)
	{
		// Mirror every status line to the debug log (timestamped) so the log is a full transcript of
		// the scan/download/cert/install flow — not just the on-screen label.
		System.Diagnostics.Trace.WriteLine($"[installer] {message}");
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_status = message;
			UpdateTvCard();
		});
	}

	/// <summary>
	/// The TV card (Core's <see cref="TvCardStates"/>) for the selected TV, with this page's state
	/// on top: the LED pulses while anything runs and the status line is the card's message. Main
	/// thread only.
	/// </summary>
	private void UpdateTvCard()
	{
		var idx = _selectedTv;
		var ip = idx >= 0 && idx < _tvIps.Count ? _tvIps[idx] : null;
		var device = idx >= 0 && idx < _tvDevices.Count ? _tvDevices[idx] : null;
		var localIp = NetworkInfo.GetLocalIPv4();

		TvCardState state;
		if (_scanning)
			state = TvCardStates.Scanning(L10n.Get);
		else if (ip is null)
			state = TvCardStates.NoTv(L10n.Get, localIp);
		else
			state = TvCardStates.ForDevice(device ?? new NetworkDevice { IpAddress = ip }, localIp, L10n.Get);

		TvCard.State = state with
		{
			Led = _busy ? TvLed.Busy : state.Led,
			Message = string.IsNullOrWhiteSpace(_status) ? state.Message : _status,
		};
	}
}
