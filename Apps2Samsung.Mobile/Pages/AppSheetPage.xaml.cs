using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Shows one catalog entry and hands back the asset to install, or null when the sheet is closed
/// without installing. The Install page pushes it modally and awaits <see cref="Result"/>.
/// </summary>
public partial class AppSheetPage : ContentPage
{
	private readonly TaskCompletionSource<Asset?> _result = new();
	private readonly List<(Asset asset, Border radio)> _rows = new();
	private Asset? _selected;
	private bool _closedWithResult;

	public Task<Asset?> Result => _result.Task;

	public AppSheetPage(GitHubRelease release, string? previewUrl, string description, string? tvName, bool tvReady)
	{
		InitializeComponent();
		TitleLabel.Text = release.Name;
		DescriptionLabel.Text = description;
		PartnerNotice.IsVisible = release.RequiresPartner;
		VersionsTitle.Text = L10n.Get(release.HasVariants ? "lblVariant" : "lblVersion");
		PublishedLabel.Text = DateTime.TryParse(release.PublishedAt, out var date) ? date.ToShortDateString() : string.Empty;

		if (!string.IsNullOrWhiteSpace(previewUrl))
		{
			PreviewImage.Source = ImageSource.FromUri(new Uri(previewUrl));
			PreviewFrame.IsVisible = true;
		}

		var defaultAsset = release.Assets.FirstOrDefault(a => a.IsDefault) ?? release.Assets.FirstOrDefault();
		var first = true;
		foreach (var asset in release.Assets)
		{
			if (!first)
				Versions.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignThemeColor("Hair") });
			first = false;
			Versions.Add(VersionRow(asset));
		}
		Select(defaultAsset);

		InstallBtn.Text = string.IsNullOrEmpty(tvName) ? L10n.Get("lblChooseTvFirst") : string.Format(L10n.Get("lblInstallOn"), tvName);
		InstallBtn.IsEnabled = tvReady && !string.IsNullOrEmpty(tvName) && _selected is not null;
	}

	private static Color DesignThemeColor(string token) => Apps2Samsung.Mobile.Resources.Styles.DesignTheme.ColorOf(token);

	private View VersionRow(Asset asset)
	{
		var radio = new Border
		{
			WidthRequest = 22, HeightRequest = 22, StrokeThickness = 2, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			Stroke = new SolidColorBrush(DesignThemeColor("Hair")), BackgroundColor = Colors.Transparent, VerticalOptions = LayoutOptions.Center,
		};
		var label = new Label { Text = asset.LongDisplayText, LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center };
		var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 48 };
		grid.Add(radio, 0, 0);
		grid.Add(label, 1, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) => Select(asset);
		grid.GestureRecognizers.Add(tap);
		_rows.Add((asset, radio));
		return grid;
	}

	private void Select(Asset? asset)
	{
		_selected = asset;
		foreach (var (a, radio) in _rows)
		{
			var on = ReferenceEquals(a, asset);
			radio.Stroke = new SolidColorBrush(DesignThemeColor(on ? "Accent" : "Hair"));
			radio.BackgroundColor = on ? DesignThemeColor("Accent") : Colors.Transparent;
		}
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
