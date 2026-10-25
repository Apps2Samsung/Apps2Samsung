using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Catalog;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Certificate;
using Apps2Samsung.Design;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Catalog;
using Apps2Samsung.Mobile.Controls;
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
	private readonly CertificateFlow _certFlow;
	private readonly WgtInstaller _installer;
	private readonly CatalogService _catalog;
	private readonly UpdateFlow _updates;
	private readonly SessionState _session;
	private readonly ISdbEngine _sdb;
	// The TVs and the one in use, shared with the other tabs (the ▾ in every top bar picks from it).
	private readonly TvSelection _tv;
	// Every catalog entry; the list shows the slice in the chosen category that matches the search.
	private IReadOnlyList<GitHubRelease> _allReleases = new List<GitHubRelease>();
	private string _query = string.Empty;
	// Categories the user expanded with "Show all".
	private readonly HashSet<string> _expanded = new();
	// The apps the user starred (#755 favorites): a group of their own at the top of the list.
	private readonly HashSet<string> _favorites = new(
		MobileSettings.FavoriteApps.Split(';', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

	private void SetFavorite(string name, bool isFavorite)
	{
		if (isFavorite) _favorites.Add(name); else _favorites.Remove(name);
		MobileSettings.FavoriteApps = string.Join(";", _favorites);
		RebuildCatalog();
	}
	private bool _catalogLoading;
	private int _catalogFailed;
	// The provider manifest, for the preview images the app sheet shows; null until it loaded.
	private ProviderManifest? _manifest;

	private bool _initialized;
	private bool _uiReady;
	// What the TV card shows besides the TV itself: the last status line, and whether an install is
	// running (download, signing, install) so its LED pulses.
	private string _status = string.Empty;
	private bool _busy;
	// The progress sheet of the running install; null when none is up.
	private InstallProgressPage? _progress;

	public InstallerPage(INetworkService networkService, ISamsungLoginService loginService,
		CertificateFlow certFlow, WgtInstaller installer, CatalogService catalog,
		UpdateFlow updates, SessionState session, ISdbEngine sdb, TvSelection tv)
	{
		InitializeComponent();
		_tv = tv;
		_networkService = networkService;
		_loginService = loginService;
		_certFlow = certFlow;
		_installer = installer;
		_catalog = catalog;
		_updates = updates;
		_session = session;
		_sdb = sdb;

		// Shows the app version (ApplicationDisplayVersion), so it stays in sync with the build.
		VersionLabel.Text = $"v{AppInfo.Current.VersionString}";
		TvCard.Tapped += OnTvCardTapped;
	}

	// A TV that isn't ready yet: the card opens the Developer Mode guide.
	private async void OnTvCardTapped(object? sender, EventArgs e)
	{
		if (_busy || !DevModeGuidePage.IsNeeded(_tv, LocalIps()))
			return;
		var guide = new DevModeGuidePage(_tv, LocalIps, fromInstall: false);
		await Navigation.PushModalAsync(guide);
		await guide.Completion;
		UpdateTvCard();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		// The TV may have been changed from another tab's ▾ while this one was hidden.
		_tv.Changed -= OnTvChanged;
		_tv.Changed += OnTvChanged;
		UpdateTvCard();
		if (_initialized)
		{
			// Settings > Installing > Show adult (18+) apps changed while this tab was hidden (#850).
			if (_listedAdult != MobileSettings.ShowAdultApps)
				RebuildCatalog();
			// "Update" on the On the TV tab (#820): that app's sheet, here where installs run.
			if (CatalogStore.TakePendingOpen() is { } pending)
				await OpenAppAsync(pending, SublineFor(pending));
			return;
		}
		_initialized = true;
		CatalogStore.Refresh = async () => { await LoadCatalogAsync(); };

		await MobileSettings.InitAsync();
		var catalog = await LoadCatalogAsync();
		// A TV already picked (this page was rebuilt for a language change) is kept; otherwise scan.
		if (!_tv.HasTv)
			await _tv.ScanAsync();
		else
			SetStatus(_tv.SelectedStatus());

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
			CatalogStore.Set(_allReleases);
			_catalogFailed = result.Failed;
		}
		catch (Exception ex)
		{
			SetStatus(string.Format(L10n.Get("statusCatalogFailed"), ex.Message));
			_allReleases = new List<GitHubRelease>();
			CatalogStore.Set(_allReleases);
		}
		finally
		{
			_catalogLoading = false;
		}

		// The manifest feeds the sheet's preview image and says which apps keep several releases (one
		// row each in the list, the releases in the sheet); a failure costs the picture and lists those
		// releases as rows of their own.
		try { _manifest = await _catalog.GetManifestAsync(); }
		catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installer] Manifest for previews failed: {ex.Message}"); }

		RebuildCatalog();
		_uiReady = true;
		return result;
	}

	// Apps whose provider keeps more than one release (Jellyfin): the list shows each once, the app
	// sheet lists the releases.
	private bool HasSeveralReleases(string name) =>
		_manifest?.Providers.Any(p => p.Take > 1 && string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)) ?? false;

	// The releases of one app, newest first.
	private List<GitHubRelease> VersionsOf(GitHubRelease release) => _allReleases
		.Where(r => string.Equals(r.Name, release.Name, StringComparison.OrdinalIgnoreCase))
		.OrderByDescending(r => DateTime.TryParse(r.PublishedAt, out var d) ? d : DateTime.MinValue)
		.ToList();

	// Whether the list was last built with the adult-only entries in (#850), to refilter on a change.
	private bool _listedAdult;

	// The catalog as listed: an app with several releases once, as its newest; without the
	// adult-only entries until the user opted in (#850).
	private List<GitHubRelease> ListedReleases()
	{
		var listed = new List<GitHubRelease>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		_listedAdult = MobileSettings.ShowAdultApps;
		foreach (var release in CommunityAppList.Visible(_allReleases, _listedAdult))
		{
			if (!HasSeveralReleases(release.Name))
				listed.Add(release);
			else if (seen.Add(release.Name))
				listed.Add(VersionsOf(release)[0]);
		}
		return listed;
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

		var listed = ListedReleases();
		var groups = CatalogGroups.Build(listed, _query, _expanded, L10n.Get, _favorites);
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
				Text = L10n.Get(listed.Count == 0 ? "statusCatalogEmpty" : "lblNoAppMatches"),
				TextColor = DesignTheme.ColorOf("Muted"), Padding = new Thickness(16, 14),
			}));
		}
		CatalogFoot.Text = _catalogFailed > 0
			? string.Format(L10n.Get("lblCatalogFootFailed"), listed.Count, _catalogFailed)
			: string.Format(L10n.Get("lblCatalogFoot"), listed.Count);
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
				NameLine(release),
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

	// The name, with the 18+ badge after it for an adult-only app (#850); such a row is only ever
	// built once the user opted in under Settings > Installing.
	private static View NameLine(GitHubRelease release)
	{
		var name = new Label { Text = release.Name, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
		if (!release.IsAdult)
			return name;
		return new HorizontalStackLayout
		{
			Spacing = 6,
			Children = { name, AdultBadge(fontSize: 11, padding: new Thickness(5, 0)) },
		};
	}

	/// <summary>The "18+" pill the list row and the app sheet share (#850).</summary>
	internal static Border AdultBadge(double fontSize, Thickness padding) => new()
	{
		StrokeThickness = 1, Stroke = new SolidColorBrush(DesignTheme.ColorOf("Warn")),
		BackgroundColor = DesignTheme.ColorOf("WarnSoft"), Padding = padding, VerticalOptions = LayoutOptions.Center,
		StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
		Content = new Label
		{
			Text = L10n.Get("lblAdultBadge"), FontSize = fontSize, FontAttributes = FontAttributes.Bold,
			TextColor = DesignTheme.ColorOf("Warn"), VerticalOptions = LayoutOptions.Center,
		},
	};

	private void OnSearchChanged(object? sender, TextChangedEventArgs e)
	{
		_query = (e.NewTextValue ?? string.Empty).Trim();
		RebuildCatalog();
	}

	// The app sheet: preview, description, the version or variant to install, Install for the
	// selected TV. Hands back the release and file, or null when closed.
	private async Task OpenAppAsync(GitHubRelease release, string subline)
	{
		if (_busy)
			return;
		var tvName = _tv.HasTv ? _tv.DisplayName : null;
		var tvReady = _tv.HasTv && _tv.DebugPortOpen;

		var several = HasSeveralReleases(release.Name);
		IReadOnlyList<GitHubRelease> versions = several ? VersionsOf(release) : new List<GitHubRelease> { release };
		if (versions.Count == 0)
			versions = new List<GitHubRelease> { release };

		// "Show all versions" stores the setting and reloads the catalog: older releases are only
		// fetched while it is on.
		Func<bool, Task<IReadOnlyList<GitHubRelease>>>? showAll = several
			? async on =>
			{
				MobileSettings.ShowAllJellyfinVersions = on;
				await LoadCatalogAsync();
				return VersionsOf(release);
			}
			: null;

		var sheet = new AppSheetPage(versions, CatalogPreviews.For(_manifest, release), subline, tvName, tvReady,
			_favorites.Contains(release.Name), SetFavorite, showAll);
		await Navigation.PushModalAsync(sheet);
		var choice = await sheet.Result;
		if (choice is not null)
			await InstallAsync(choice.Release, choice.Asset, null, choice.NewPackageId);
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

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_tv.Changed -= OnTvChanged;
	}

	// The list, the selection or the scan changed (here or from another tab's ▾): the scan's or the
	// picker's word goes on the card, unless an install is talking.
	private void OnTvChanged()
	{
		if (!_busy && !string.IsNullOrEmpty(_tv.Status))
			_status = _tv.Status;
		UpdateTvCard();
	}

	// The TV pill in the top bar (only with more than one TV): the Switch TV sheet.
	private async void OnChangeTvClicked(object? sender, EventArgs e)
	{
		if (_busy)
			return;
		await TvPicker.ChooseAsync(this, _tv);
	}

	// The ⋯ in the top bar: what used to be the chips on the TV card, plus adding a TV the scan
	// didn't find. TV information and the toolbox need a TV, so they only show with one.
	private async void OnMoreClicked(object? sender, EventArgs e)
	{
		var items = new List<MoreMenuItem>
		{
			new(L10n.Get("lblRescanForTvs"), MoreMenu.IconRescan),
			new(L10n.Get("lblAddTvByIp"), MoreMenu.IconAdd),
		};
		if (_tv.HasTv)
		{
			items.Add(new(L10n.Get("lblTvInformation"), MoreMenu.IconInfo));
			items.Add(new(L10n.Get("lblToolbox"), MoreMenu.IconToolbox));
		}
		switch (await MoreMenu.ShowAsync(this, items.ToArray()))
		{
			case 0: await RescanAsync(); break;
			case 1: await AddTvByIpAsync(); break;
			case 2: await ShowDeviceInfoAsync(); break;
			case 3: await ShowTvToolboxAsync(); break;
		}
	}

	private async Task RescanAsync()
	{
		if (!_busy)
			await _tv.ScanAsync();
	}

	private async Task AddTvByIpAsync()
	{
		if (!_busy)
			await TvPicker.AddByIpAsync(this, _tv);
	}

	// The buttons on the no-TV card.
	private async void OnNoTvRescanClicked(object? sender, EventArgs e) => await RescanAsync();
	private async void OnNoTvAddClicked(object? sender, EventArgs e) => await AddTvByIpAsync();

	// The app's own page in the system Settings, where the Local Network switch lives on iOS (#824).
	private void OnNoTvSettingsClicked(object? sender, EventArgs e) => AppInfo.Current.ShowSettingsUI();

	private async Task ShowDeviceInfoAsync()
	{
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		await Navigation.PushAsync(new DeviceInfoPage(_sdb, _tv.Ip!, _tv.Label, _tv.DebugPortOpen));
	}

	// Same story as the remote: the toolbox rides the network channel, so it is offered for any TV
	// on the network, Developer Mode or not.
	private async Task ShowTvToolboxAsync()
	{
		if (!_tv.HasTv)
		{
			SetStatus(L10n.Get("statusSelectTvFirst"));
			return;
		}
		var tvIp = _tv.Ip!;
		var label = _tv.Label;

		// The toolbox's debug agent is a .wgt like any other: the normal pipeline (certificate, resign,
		// push) puts it on the TV when the set doesn't have it yet (#34).
		Func<string, Action<string>, Task<bool>> installWgt = async (wgtPath, report) =>
		{
			var (cert, _) = await _certFlow.RunAsync(Navigation, tvIp, label, false, report, new CancellationTokenSource());
			await _installer.InstallAsync(tvIp, wgtPath, cert, report,
				confirmRemove: () => ConfirmRemoveAsync(Path.GetFileNameWithoutExtension(wgtPath)));
			return true;
		};

		await Navigation.PushAsync(new TvToolboxPage(_sdb, tvIp, label, installWgt));
	}

	// Downloads (or takes the picked file), provisions the certificate, installs on the selected
	// TV. The progress sheet walks through the steps; the TV card's LED pulses for the duration and
	// keeps the status line for when the sheet is swiped away.
	/// <summary>
	/// The values a community package asks for before it installs (#791): the sheet, prefilled
	/// with what was given last time, then stored for the patcher and for the next update. False
	/// when the user cancelled. A package whose catalog entry stopped asking forgets what was
	/// stored, so the patcher stays out of it.
	/// </summary>
	private async Task<bool> CollectUserSettingsAsync(GitHubRelease release, Asset asset)
	{
		if (asset.UserSettings is not { } spec)
		{
			var cleared = UserSettingsStore.Remove(MobileSettings.AppUserSettingsJson, asset.FileName);
			if (cleared != MobileSettings.AppUserSettingsJson)
				MobileSettings.AppUserSettingsJson = cleared;
			return true;
		}

		var current = UserSettingsStore.Get(MobileSettings.AppUserSettingsJson, asset.FileName)?.Values
			?? new Dictionary<string, string>();
		var sheet = new UserSettingsSheetPage(release.Name, spec, current);
		await Navigation.PushModalAsync(sheet);
		var values = await sheet.Result;
		if (values is null)
		{
			SetStatus(L10n.Get("statusInstallCancelled"));
			return false;
		}
		MobileSettings.AppUserSettingsJson = UserSettingsStore.Set(MobileSettings.AppUserSettingsJson, asset.FileName, spec, values);
		return true;
	}

	/// <param name="newPackageId">The sheet's "Install under a new package id" (#839); a rename made
	/// earlier for this TV applies either way.</param>
	private async Task InstallAsync(GitHubRelease? release, Asset? asset, string? customPath, bool newPackageId = false)
	{
		if (_tv.Selected is not { } tv)
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

		var tvIp = tv.Ip;

		// The device list is a scan snapshot; re-read the TV's Developer-Mode fields now so a
		// Developer-Mode IP corrected on the TV since the scan isn't still flagged as a mismatch.
		await _tv.RefreshSelectedAsync();

		// Shared pre-install guards (Core): a TV that still needs a restart, Developer Mode off, a
		// Developer-Mode IP pointing at another device or typed back to front, a TV on another subnet.
		// Same checks and wording as the desktop head — each one is Continue/Stop, never a hard block.
		var guardResult = InstallGuards.Evaluate(
			tv.Device,
			new InstallGuardOptions
			{
				LocalIps = LocalIps(),
				ConfiguredLocalIp = NetworkInfo.GetLocalIPv4(),
			},
			_networkService);

		// The Developer-Mode guards share one answer: the guide, which re-checks the TV while it is
		// open and turns into Continue once it is fixed. The rest stay a Continue/Stop question.
		var developerModeGuards = guardResult.Guards.Where(g => g.Kind is InstallGuardKind.RestartRequired
			or InstallGuardKind.DeveloperModeOff or InstallGuardKind.DeveloperIpReversed or InstallGuardKind.DeveloperIpMismatch).ToList();
		if (developerModeGuards.Count > 0)
		{
			var guide = new DevModeGuidePage(_tv, LocalIps, fromInstall: true);
			await Navigation.PushModalAsync(guide);
			var goOn = await guide.Completion;
			UpdateTvCard();
			if (!goOn)
				return;
		}

		foreach (var guard in guardResult.Guards.Except(developerModeGuards))
		{
			var message = L10n.Get(guard.MessageKey);
			bool continueAnyway = await DisplayAlert(
				L10n.Get(guard.TitleKey),
				string.IsNullOrEmpty(guard.Detail) ? message : $"{message}\n\n{guard.Detail}",
				L10n.Get("keyContinue"), L10n.Get("keyStop"));
			if (!continueAnyway)
				return;
		}

		tvIp = guardResult.CorrectedTvIp ?? tvIp;

		// A community package that needs the user's own values (#791): the sheet first.
		if (!custom && !await CollectUserSettingsAsync(release!, asset!))
			return;

		var appName = custom ? Path.GetFileNameWithoutExtension(customPath!) : release!.Name;
		var appTitle = custom || string.IsNullOrWhiteSpace(release!.TagName) || !HasSeveralReleases(release.Name)
			? appName
			: $"{appName} {release.TagName}";
		// Auto-request Partner signing if the selected app's manifest declares cert_level: partner.
		var requirePartner = !custom && release!.RequiresPartner;

		_busy = true;
		_tv.IsLocked = true;
		UpdateTvCard();
		// Not disposed: it has no timer, and Cancel on a disposed source would throw from the sheet.
		var cancel = new CancellationTokenSource();
		_progress = new InstallProgressPage(appTitle, custom ? null : release!.Category, _tv.DisplayName, MobileSettings.OpenAfterInstall);
		_progress.CancelRequested += (_, _) => cancel.Cancel();
		await Navigation.PushModalAsync(_progress);
		// The install must finish in the foreground: iOS suspends a backgrounded app, and a screen
		// timeout halfway through the push would leave the TV without the old copy or the new one.
		DeviceDisplay.Current.KeepScreenOn = true;
		string? wgtPath = null;
		// True while the package downloads, so a connection failure there gets its own message.
		bool downloading = false;
		try
		{
			// Download first so we can inspect the package's declared privileges before signing.
			SetStep(InstallStep.Download);
			downloading = !custom;
			wgtPath = custom ? customPath! : await _installer.DownloadAsync(asset!.DownloadUrl, SetStatus, cancel.Token);
			downloading = false;
			cancel.Token.ThrowIfCancellationRequested();

			// Partner if the manifest declared it OR the .wgt itself needs a partner-level privilege.
			var needsPartner = requirePartner || WgtPrivileges.RequiresPartner(wgtPath);
			// Provisioning reuses a valid cert already covering this TV and only triggers a Samsung
			// sign-in when it must (re)generate — so no login prompt when a cert is already in place.
			SetStep(InstallStep.Certificate);
			var (cert, _) = await _certFlow.RunAsync(Navigation, tvIp, _tv.DisplayName, needsPartner, SetStatus, cancel);
			cancel.Token.ThrowIfCancellationRequested();

			// The signing certificate's validity period may not have started yet — a Samsung TV
			// rejects such a package ("Certificate in signature is not valid yet"). Hold the install
			// behind a countdown to the moment it becomes valid instead of pushing something the TV
			// will refuse; the loop re-runs the check once the user chooses to continue.
			while (true)
			{
				try
				{
					await _installer.InstallAsync(tvIp, wgtPath, cert, SetStatus, SetStep, cancel.Token,
						confirmRemove: () => ConfirmRemoveAsync(appName),
						packageId: new PackageIdPolicy(cert.Duid, newPackageId));
					break;
				}
				catch (CertificateNotYetValidException notYetValid)
				{
					SetStatus(L10n.Get("certificateWaiting"));
					var wait = new CertificateWaitPage(notYetValid.Result);
					await Navigation.PushModalAsync(wait);
					if (!await wait.Completion)
					{
						FailProgress(L10n.Get("certificateWaitCancelled"));
						return;
					}
				}
			}

			SetStatus("✓ " + string.Format(L10n.Get("statusInstalledOpenApps"), appName));
			await CloseProgressAsync();
			await Navigation.PushModalAsync(new InstallCompletePage(appName));
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			// Cancel on the sheet: it stops before the old copy is removed or the package pushed, so the
			// TV still has what it had and there is nothing to report but that.
			SetStatus(L10n.Get("statusInstallCancelled"));
			await CloseProgressAsync();
		}
		catch (Exception ex) when (downloading && NetworkFailure.LocalizationKey(ex) is { } networkKey)
		{
			// The connection, not the app: an HttpClient timeout is a TaskCanceledException too, and
			// used to fall into the sign-in catch below and say "sign-in cancelled" (#809).
			System.Diagnostics.Trace.WriteLine($"[installer] Download failed: {ex}");
			FailProgress($"{L10n.Get("DownloadFailed")} {L10n.Get(networkKey)}");
		}
		catch (TaskCanceledException)
		{
			FailProgress(L10n.Get("statusSignInCancelled"));
		}
		catch (UserSettingsPatchException ex)
		{
			// The package changed upstream: the text a user setting replaces is gone (#791).
			System.Diagnostics.Trace.WriteLine($"[UserSettings] {ex.Message}");
			FailProgress(ex.Find is null
				? string.Format(L10n.Get("statusUserSettingsFileMissing"), ex.PackageFile, ex.File)
				: string.Format(L10n.Get("statusUserSettingsFindMissing"), ex.PackageFile, ex.Find, ex.File));
		}
		catch (RemovalDeclinedException)
		{
			// The user kept the installed copy (#825): the TV is as it was, say so without "failed".
			FailProgress(string.Format(L10n.Get("removeDeclined"), appName));
		}
		catch (SamsungAccountEmailMissingException)
		{
			// The Samsung account has no email, so the distributor CSR has nothing to put in its
			// emailAddress field — this used to surface as a bare NullReferenceException (issue #606).
			// Only the user can fix it, on their Samsung account page, so offer to open it.
			SetStatus(L10n.Get("statusSamsungAccountNoEmail"));
			await CloseProgressAsync();
			await Navigation.PushModalAsync(new AccountEmailSheetPage());
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[installer] Install failed: {ex}");
			FailProgress(string.Format(L10n.Get("statusInstallFailed"), ex.Message));
		}
		finally
		{
			DeviceDisplay.Current.KeepScreenOn = false;
			// Clean up the staged package unless the user opted to keep it. For a custom install
			// this is the cache copy, never the user's original file.
			if (wgtPath is not null && !MobileSettings.KeepWgtFile)
			{
				try { File.Delete(wgtPath); } catch { /* best-effort */ }
			}

			_busy = false;
			_tv.IsLocked = false;
			// A failed install leaves the sheet up with the reason and Close; it stops listening here.
			_progress = null;
			UpdateTvCard();
		}
	}

	// The step the install reached, on the progress sheet (any thread).
	private void SetStep(InstallStep step) => SetStep(step, null);

	private void SetStep(InstallStep step, int? percent)
	{
		var sheet = _progress;
		if (sheet is not null)
			MainThread.BeginInvokeOnMainThread(() => sheet.SetStep(step, percent));
	}

	// A failure: the status line, and the sheet turns the current step red with the reason.
	// The remove-and-reinstall recovery erases the installed app's data on the TV, so it asks first
	// (#825). Shown from the progress sheet, the topmost page while an install runs.
	private Task<bool> ConfirmRemoveAsync(string appName) =>
		MainThread.InvokeOnMainThreadAsync(() =>
		{
			Page host = _progress ?? (Page)this;
			return host.DisplayAlert(
				string.Format(L10n.Get("confirmRemoveTitle"), appName),
				string.Format(L10n.Get("confirmRemove"), appName),
				L10n.Get("keyRemoveAndInstall"),
				L10n.Get("lblCancel"));
		});

	private void FailProgress(string message)
	{
		System.Diagnostics.Trace.WriteLine($"[installer] {message}");
		var sheet = _progress;
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_status = message;
			UpdateTvCard();
			sheet?.Fail(message);
		});
	}

	private async Task CloseProgressAsync()
	{
		var sheet = _progress;
		_progress = null;
		if (sheet is not null)
			await MainThread.InvokeOnMainThreadAsync(sheet.CloseAsync);
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

	private void SetStatus(string message)
	{
		// Mirror every status line to the debug log (timestamped) so the log is a full transcript of
		// the scan/download/cert/install flow — not just the on-screen label.
		System.Diagnostics.Trace.WriteLine($"[installer] {message}");
		var sheet = _progress;
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_status = message;
			UpdateTvCard();
			sheet?.Report(message);
		});
	}

	/// <summary>
	/// The TV card (Core's <see cref="TvCardStates"/>, via <see cref="TvSelection"/>) and the top bar
	/// for the selected TV, with this page's state on top: the LED pulses while an install runs and
	/// the status line is the card's message. Main thread only.
	/// </summary>
	private void UpdateTvCard()
	{
		var state = _tv.CardState();
		TopBar.ShowTv(_tv.HasTv || _tv.IsScanning ? state : state with { Name = L10n.Get("lblNoTvFound") });
		TopBar.TvCount = _tv.Tvs.Count;
		var noTv = !_tv.HasTv && !_tv.IsScanning;
		if (noTv)
		{
			var blocked = _tv.LocalNetworkBlocked;
			TopBar.Subtitle = L10n.Get(blocked ? "lblNoTvBlockedSubtitle" : "lblNoTvFoundHint");
			TopBar.Status = TopBarStatus.Warn;
			NoTvTitle.Text = L10n.Get(blocked ? "lblNoTvBlockedTitle" : "lblNoTvYetTitle");
			NoTvHint.Text = L10n.Get(blocked ? "lblNoTvBlockedHint" : "lblNoTvYetHint");
			NoTvAddBtn.IsVisible = !blocked;
			NoTvSettingsBtn.IsVisible = blocked;
		}
		TvCard.IsVisible = !noTv;
		NoTvCard.IsVisible = noTv;
		if (_busy)
			TopBar.Status = TopBarStatus.Busy;

		TvCard.State = state with
		{
			Led = _busy ? TvLed.Busy : state.Led,
			Message = string.IsNullOrWhiteSpace(_status) ? state.Message : _status,
		};
	}
}
