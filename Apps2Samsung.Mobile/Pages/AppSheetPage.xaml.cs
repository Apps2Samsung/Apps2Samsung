using Apps2Samsung.Mobile.Controls;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the app sheet hands back: the release and the file of it to install, and whether
/// it goes on the TV under a new package id (#839).</summary>
/// <param name="NewPackageId">"Install under a new package id" (#839).</param>
/// <param name="OwnPackageId">"Back to its own package id" (#839): forget the rename remembered for the TV.</param>
public sealed record AppSheetChoice(GitHubRelease Release, Asset Asset, bool NewPackageId = false, bool OwnPackageId = false);

/// <summary>
/// Shows one catalog entry and hands back what to install, or null when the sheet is closed
/// without installing. The Install page pushes it modally and awaits <see cref="Result"/>.
/// <para>An app the catalog keeps several releases of (Jellyfin) lists them all under Version;
/// "Show all versions" then decides whether the older ones are fetched at all.</para>
/// </summary>
public partial class AppSheetPage : ContentPage
{
	private readonly TaskCompletionSource<AppSheetChoice?> _result = new();
	private readonly List<(GitHubRelease release, Asset asset, Border radio)> _rows = new();
	private AppSheetChoice? _selected;
	private bool _closedWithResult;
	private readonly Action<string, bool>? _setFavorite;
	private readonly Func<bool, Task<IReadOnlyList<GitHubRelease>>>? _showAllVersions;
	// A tap on the developer's name closes the sheet and searches the catalog for them (#888).
	private readonly Action<string>? _searchAuthor;
	private readonly string _name;
	private readonly bool _tvReady;
	// The selected TV's Tizen version, when known: "Needs Tizen X" and no Install for a build it is too old for (TV-Store#47).
	private readonly Version? _tvVersion;
	private readonly Asset? _openOn;
	private readonly string? _tvName;
	// Another source's copy of this app tapped under "Also offered by" (#1018): the sheet closes, the caller opens that one.
	private readonly Action<GitHubRelease>? _openOther;
	// Opened for an app the TV runs another version of: the button says Update (#888).
	private readonly bool _isUpdate;
	private bool _isFavorite;
	private bool _loadingVersions;
	// False while the constructor sets the switch, so that doesn't count as the user flipping it.
	private bool _ready;

	public Task<AppSheetChoice?> Result => _result.Task;

	/// <param name="versions">The releases of this app, newest first; usually just the one.</param>
	/// <param name="showAllVersions">For an app with older releases on offer: stores the setting and
	/// reloads, handing back the releases to list. Null hides the switch.</param>
	/// <param name="subline">The list row's info line: the category, and the variant count for a grouped app.</param>
	/// <param name="searchAuthor">Called with the developer's name when the user taps it; null leaves the name plain.</param>
	/// <param name="alsoOffered">The same app from the other sources in the catalog (#1018), one entry per source.</param>
	/// <param name="openOther">Opens the sheet of one of <paramref name="alsoOffered"/>, once this one is closed.</param>
	public AppSheetPage(IReadOnlyList<GitHubRelease> versions, string? previewUrl, string subline, string? tvName, bool tvReady,
		bool isFavorite = false, Action<string, bool>? setFavorite = null,
		Func<bool, Task<IReadOnlyList<GitHubRelease>>>? showAllVersions = null, bool isUpdate = false,
		Action<string>? searchAuthor = null, Version? tvVersion = null, Asset? openOn = null, CatalogIcons? icons = null,
		IReadOnlyList<GitHubRelease>? alsoOffered = null, Action<GitHubRelease>? openOther = null, bool fromLink = false)
	{
		InitializeComponent();
		FromLinkChip.IsVisible = fromLink;
		_tvVersion = tvVersion;
		_openOn = openOn;
		var release = versions[0];
		// The same tile as the list row: the letter, or the app's icon from the catalog (#1000).
		TileSlot.Content = InstallerPage.AppTile(release, icons);
		_name = release.Name;
		_isFavorite = isFavorite;
		_setFavorite = setFavorite;
		_showAllVersions = showAllVersions;
		_searchAuthor = searchAuthor;
		_openOther = openOther;
		_tvName = tvName;
		_isUpdate = isUpdate;
		_tvReady = tvReady;
		ShowFavorite();
		TitleLabel.Text = release.Name;
		// What the app is, from the catalog (#835); the paragraph stays hidden when the catalog has none.
		DescriptionLabel.Text = release.Description;
		DescriptionLabel.IsVisible = !string.IsNullOrWhiteSpace(release.Description);
		SublineLabel.Text = subline;
		ShowSource(release);
		PartnerNotice.IsVisible = release.RequiresPartner;
		AdultBadge.IsVisible = release.IsAdult;
		RootBadge.IsVisible = release.IsRoot;
		// #1016: several flags can apply at once; each shows its own badge.
		SelfUpdatingBadge.IsVisible = SelfUpdatingLabel.IsVisible = release.IsSelfUpdating;
		ClosedSourceBadge.IsVisible = release.IsClosedSource;
		ShowSourceLine(release);
		ShowAlsoOffered(alsoOffered);
		VersionsTitle.Text = L10n.Get(release.HasVariants ? "lblVariant" : "lblVersion");
		// The way back to the package's own id (#839), only for an app renamed on some TV before.
		OwnIdRow.IsVisible = versions.Any(v => v.Assets.Any(a =>
			Apps2Samsung.Packaging.PackageIdRenameStore.IsRenamedAnywhere(MobileSettings.PackageIdRenamesJson, a.PackageId)));

		if (!string.IsNullOrWhiteSpace(previewUrl))
		{
			PreviewImage.Source = ImageSource.FromUri(new Uri(previewUrl));
			PreviewFrame.IsVisible = true;
		}

		// The switch takes the publish date's place in the header.
		if (showAllVersions is not null)
		{
			ShowAllSwitch.IsToggled = MobileSettings.ShowAllJellyfinVersions;
			ShowAllSwitch.IsVisible = ShowAllLabel.IsVisible = true;
			PublishedLabel.IsVisible = false;
		}

		ShowVersions(versions);
		_ready = true;
	}

	private static Color DesignThemeColor(string token) => Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token);

	// "by owner · Source code", the link opening the app's repository; hidden without one.
	private void ShowSource(GitHubRelease release)
	{
		if (string.IsNullOrEmpty(release.SourceUrl))
			return;
		var url = release.SourceUrl;
		var link = new Span { Text = L10n.Get("lblSourceCode"), TextColor = DesignThemeColor("Accent") };
		link.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () => await LinkOpener.OpenAsync(url)) });
		var text = new FormattedString();
		if (!string.IsNullOrEmpty(release.Author))
		{
			var author = release.Author;
			var by = new Span { Text = string.Format(L10n.Get("lblByAuthor"), author) };
			if (_searchAuthor is { } search)
			{
				// Every app by this developer: close, then let the Install page fill its search box (#888).
				by.TextColor = DesignThemeColor("Accent");
				by.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(async () =>
				{
					await Navigation.PopModalAsync();
					search(author);
				}) });
			}
			text.Spans.Add(by);
			text.Spans.Add(new Span { Text = " · " });
		}
		text.Spans.Add(link);
		SourceLabel.FormattedText = text;
		SourceLabel.IsVisible = true;
	}

	// Where the app comes from (#1018), under the title; the official catalog has no line. A
	// repository that could not be checked also says why nothing installs from it.
	private void ShowSourceLine(GitHubRelease release)
	{
		var line = CatalogSourceLabels.Line(release.Source, L10n.Get);
		SourceLineSlot.Content = SourceLineView.Build(line, 14);
		SourceLineSlot.IsVisible = line.IsVisible;
		BlockedHint.Text = line.Kind == SourceLineKind.Blocked ? line.Tip : string.Empty;
	}

	// "Also offered by": one row per other source, its name with the source's icon, the version it
	// has; a tap closes this sheet and opens that copy's (#1018).
	private void ShowAlsoOffered(IReadOnlyList<GitHubRelease>? others)
	{
		AlsoOfferedRows.Clear();
		if (others is null || others.Count == 0 || _openOther is null)
			return;
		var first = true;
		foreach (var other in others)
		{
			if (!first)
				AlsoOfferedRows.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignThemeColor("Hair") });
			first = false;
			AlsoOfferedRows.Add(OfferRow(other));
		}
		AlsoOfferedSection.IsVisible = true;
	}

	private View OfferRow(GitHubRelease other)
	{
		var line = CatalogSourceLabels.Line(other.Source, L10n.Get);
		var name = new HorizontalStackLayout { Spacing = 6 };
		if (line.Kind == SourceLineKind.Repository)
			name.Children.Add(SourceLineView.Icon(SourceLineView.ShieldIcon, "Accent", 14));
		else if (line.Kind == SourceLineKind.Blocked)
			name.Children.Add(SourceLineView.Icon(SourceLineView.LockIcon, "Muted", 14));
		else if (line.Kind == SourceLineKind.Unverified)
			name.Children.Add(SourceLineView.Icon(SourceLineView.WarnIcon, "Warn", 14));
		name.Children.Add(new Label
		{
			Text = CatalogSourceLabels.Name(other.Source, L10n.Get), FontAttributes = FontAttributes.Bold,
			LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center,
		});
		var detail = string.IsNullOrWhiteSpace(other.TagName) ? string.Empty : other.TagName;
		if (line.Kind == SourceLineKind.Blocked)
			detail = string.IsNullOrEmpty(detail) ? line.Text : $"{detail} · {line.Text}";
		var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { name } };
		if (!string.IsNullOrEmpty(detail))
			text.Children.Add(new Label { Text = detail, FontSize = 13, TextColor = DesignThemeColor("Muted"), LineBreakMode = LineBreakMode.TailTruncation });
		var chevron = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M1,1 L7,6.5 L1,12")!,
			Stroke = new SolidColorBrush(DesignThemeColor("Ink")), StrokeThickness = 2, Opacity = 0.35,
			WidthRequest = 8, HeightRequest = 13, VerticalOptions = LayoutOptions.Center,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 52,
		};
		grid.Add(text, 0, 0);
		grid.Add(chevron, 1, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += async (_, _) =>
		{
			_closedWithResult = true;
			_result.TrySetResult(null);
			await Navigation.PopModalAsync();
			_openOther?.Invoke(other);
		};
		grid.GestureRecognizers.Add(tap);
		return grid;
	}

	// One row per file; with several releases each row is "tag · file" (the file only when a release
	// has more than one). The default file of the newest release is preselected.
	private void ShowVersions(IReadOnlyList<GitHubRelease> versions)
	{
		Versions.Clear();
		_rows.Clear();
		var several = versions.Count > 1;
		PublishedLabel.Text = DateTime.TryParse(versions[0].PublishedAt, out var date) ? date.ToShortDateString() : string.Empty;

		var first = true;
		foreach (var release in versions)
		{
			foreach (var asset in release.Assets)
			{
				if (!first)
					Versions.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignThemeColor("Hair") });
				first = false;
				var label = !several ? asset.LongDisplayText
					: release.Assets.Count > 1 ? $"{release.TagName} · {asset.LongDisplayText}"
					: release.TagName;
				// The Tizen this build asks for, from the catalog (TV-Store#47).
				if (asset.HasRequiredVersion)
					label += " · " + asset.RequiredVersionText;
				Versions.Add(VersionRow(release, asset, label));
			}
		}

		var newest = versions[0];
		// The file the sheet was opened on (the variant the TV got, #975) when it is one of the
		// newest release's; else land on a build this TV runs when the default is too new for it (TV-Store#47).
		var defaultAsset = newest.Assets.FirstOrDefault(a => _openOn is not null && string.Equals(a.FileName, _openOn.FileName, StringComparison.OrdinalIgnoreCase))
			?? TizenRequirement.FirstFitting(newest.Assets, newest.Assets.FirstOrDefault(a => a.IsDefault) ?? newest.Assets.FirstOrDefault(), _tvVersion);
		Select(defaultAsset is null ? null : new AppSheetChoice(newest, defaultAsset));

		// An app the TV runs an older (or other) version of: the same install, called an update (#888).
		InstallBtn.Text = string.IsNullOrEmpty(_tvName) ? L10n.Get("lblChooseTvFirst")
			: string.Format(L10n.Get(_isUpdate ? "lblUpdateOn" : "lblInstallOn"), _tvName);
		UpdateInstallButton();
	}

	// Install is on when the TV is ready and the selected build runs on it; the badge by the title
	// and the hint above the button say what the build asks for (TV-Store#47).
	private void UpdateInstallButton()
	{
		var asset = _selected?.Asset;
		var needs = asset is not null && TizenRequirement.NeedsNewer(asset, _tvVersion);
		// A repository that could not be checked just now (#1018): listed, but nothing installs from it.
		var blocked = _selected is { Release.Source.CanInstall: false };
		InstallBtn.IsEnabled = _tvReady && !string.IsNullOrEmpty(_tvName) && _selected is not null && !needs && !blocked;
		BlockedHintFrame.IsVisible = blocked && !string.IsNullOrEmpty(BlockedHint.Text);
		TizenBadge.IsVisible = asset is { HasRequiredVersion: true };
		if (asset is { HasRequiredVersion: true })
		{
			TizenBadgeLabel.Text = string.Format(L10n.Get(needs ? "lblNeedsTizenBadge" : "lblTizenOrNewer"), asset.RequiredVersion);
			TizenBadge.Stroke = new SolidColorBrush(DesignThemeColor(needs ? "Warn" : "Hair"));
			TizenBadge.BackgroundColor = DesignThemeColor(needs ? "WarnSoft" : "Fill");
			TizenBadgeLabel.TextColor = DesignThemeColor(needs ? "Warn" : "Muted");
		}
		TizenHintFrame.IsVisible = needs;
		if (needs && asset is not null)
			TizenHint.Text = string.Format(L10n.Get("hintNeedsTizen"), _selected!.Release.Name, asset.RequiredVersion, _tvName, _tvVersion);
	}

	private View VersionRow(GitHubRelease release, Asset asset, string text)
	{
		var radio = new Border
		{
			WidthRequest = 22, HeightRequest = 22, StrokeThickness = 2, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			Stroke = new SolidColorBrush(DesignThemeColor("Hair")), BackgroundColor = Colors.Transparent, VerticalOptions = LayoutOptions.Center,
		};
		var label = new Label { Text = text, LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center };
		var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 48 };
		grid.Add(radio, 0, 0);
		grid.Add(label, 1, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) => Select(new AppSheetChoice(release, asset));
		grid.GestureRecognizers.Add(tap);
		_rows.Add((release, asset, radio));
		return grid;
	}

	private void Select(AppSheetChoice? choice)
	{
		_selected = choice;
		foreach (var (release, asset, radio) in _rows)
		{
			var on = choice is not null && ReferenceEquals(release, choice.Release) && ReferenceEquals(asset, choice.Asset);
			radio.Stroke = new SolidColorBrush(DesignThemeColor(on ? "Accent" : "Hair"));
			radio.BackgroundColor = on ? DesignThemeColor("Accent") : Colors.Transparent;
		}
		if (_ready)
			UpdateInstallButton();
	}

	private async void OnShowAllToggled(object? sender, ToggledEventArgs e)
	{
		if (!_ready || _showAllVersions is null || _loadingVersions)
			return;
		_loadingVersions = true;
		ShowAllSwitch.IsEnabled = false;
		InstallBtn.IsEnabled = false;
		try
		{
			var versions = await _showAllVersions(e.Value);
			if (versions.Count > 0)
				ShowVersions(versions);
		}
		finally
		{
			ShowAllSwitch.IsEnabled = true;
			UpdateInstallButton();
			_loadingVersions = false;
		}
	}

	private void ShowFavorite()
	{
		FavoriteBtn.Text = _isFavorite ? "★" : "☆";
		FavoriteBtn.TextColor = DesignThemeColor(_isFavorite ? "Accent" : "Ink");
		SemanticProperties.SetDescription(FavoriteBtn, L10n.Get(_isFavorite ? "lblRemoveFavorite" : "lblAddFavorite"));
	}

	private void OnFavoriteClicked(object? sender, EventArgs e)
	{
		_isFavorite = !_isFavorite;
		ShowFavorite();
		_setFavorite?.Invoke(_name, _isFavorite);
	}

	private async void OnInstallClicked(object? sender, EventArgs e)
	{
		_closedWithResult = true;
		_result.TrySetResult(_selected is null ? null : _selected with { NewPackageId = NewIdSwitch.IsToggled, OwnPackageId = OwnIdSwitch.IsToggled });
		await Navigation.PopModalAsync();
	}

	// One or the other: a fresh id and the package's own id exclude each other.
	private void OnNewIdToggled(object? sender, ToggledEventArgs e) { if (e.Value) OwnIdSwitch.IsToggled = false; }
	private void OnOwnIdToggled(object? sender, ToggledEventArgs e) { if (e.Value) NewIdSwitch.IsToggled = false; }

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_closedWithResult)
			_result.TrySetResult(null);
	}
}
