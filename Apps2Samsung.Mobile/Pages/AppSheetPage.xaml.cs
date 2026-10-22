using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the app sheet hands back: the release and the file of it to install.</summary>
public sealed record AppSheetChoice(GitHubRelease Release, Asset Asset);

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
	private readonly string _name;
	private readonly bool _tvReady;
	private readonly string? _tvName;
	private bool _isFavorite;
	private bool _loadingVersions;
	// False while the constructor sets the switch, so that doesn't count as the user flipping it.
	private bool _ready;

	public Task<AppSheetChoice?> Result => _result.Task;

	/// <param name="versions">The releases of this app, newest first; usually just the one.</param>
	/// <param name="showAllVersions">For an app with older releases on offer: stores the setting and
	/// reloads, handing back the releases to list. Null hides the switch.</param>
	public AppSheetPage(IReadOnlyList<GitHubRelease> versions, string? previewUrl, string description, string? tvName, bool tvReady,
		bool isFavorite = false, Action<string, bool>? setFavorite = null,
		Func<bool, Task<IReadOnlyList<GitHubRelease>>>? showAllVersions = null)
	{
		InitializeComponent();
		var release = versions[0];
		_name = release.Name;
		_isFavorite = isFavorite;
		_setFavorite = setFavorite;
		_showAllVersions = showAllVersions;
		_tvName = tvName;
		_tvReady = tvReady;
		ShowFavorite();
		TitleLabel.Text = release.Name;
		DescriptionLabel.Text = description;
		PartnerNotice.IsVisible = release.RequiresPartner;
		VersionsTitle.Text = L10n.Get(release.HasVariants ? "lblVariant" : "lblVersion");

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
				Versions.Add(VersionRow(release, asset, label));
			}
		}

		var newest = versions[0];
		var defaultAsset = newest.Assets.FirstOrDefault(a => a.IsDefault) ?? newest.Assets.FirstOrDefault();
		Select(defaultAsset is null ? null : new AppSheetChoice(newest, defaultAsset));

		InstallBtn.Text = string.IsNullOrEmpty(_tvName) ? L10n.Get("lblChooseTvFirst") : string.Format(L10n.Get("lblInstallOn"), _tvName);
		InstallBtn.IsEnabled = _tvReady && !string.IsNullOrEmpty(_tvName) && _selected is not null;
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
			InstallBtn.IsEnabled = _tvReady && !string.IsNullOrEmpty(_tvName) && _selected is not null;
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
		_result.TrySetResult(_selected);
		await Navigation.PopModalAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_closedWithResult)
			_result.TrySetResult(null);
	}
}
