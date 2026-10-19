using Apps2Samsung.Catalog;
using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings > App customization: a row per app that takes settings at install (Jellyfin, TizenTube,
/// TVApp, ImmiTV, Camera PiP), each with a one-line summary of what is set, and App icons for all.
/// </summary>
public partial class SettingsAppsPage : ContentPage
{
	public SettingsAppsPage()
	{
		InitializeComponent();
	}

	// Rebuilt on every visit: a sub-page may have changed what the summaries say.
	protected override void OnAppearing()
	{
		base.OnAppearing();
		AppRows.Clear();
		Add("Jellyfin", AppCategories.Media,
			Or(MobileSettings.JellyfinServerUrl, L10n.Get("lblConfigureJellyfinServer")),
			() => new JellyfinSettingsPage());
		Add("TizenTube", AppCategories.Streaming,
			L10n.Get(MobileSettings.TizenTubeProxyOverride ? "lblTizenTubeProxyOn" : "lblTizenTubeProxyOff"),
			() => new SettingsTizenTubePage());
		Add("TVApp", AppCategories.Iptv,
			string.Format(L10n.Get("lblChannelCount"), MobileSettings.GetTvAppChannels().Count),
			() => new SettingsTvAppPage());
		Add("ImmiTV", AppCategories.Media,
			Or(MobileSettings.ImmiTvServerUrl, L10n.Get("hintImmiTv")),
			() => new SettingsImmiTvPage());
		Add(L10n.Get("lblCameraPip"), AppCategories.Tools,
			L10n.Get("hintCameraPip"),
			() => new SettingsCameraPipPage());
	}

	private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

	// Letter tile, name over the summary, chevron; tapping pushes the app's page.
	private void Add(string name, string category, string summary, Func<Page> page)
	{
		if (AppRows.Children.Count > 0)
			AppRows.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(72, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });

		var tile = new Border
		{
			WidthRequest = 44, HeightRequest = 44, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			BackgroundColor = DesignTheme.ColorOf(AppTiles.ColorToken(category, name)),
			Content = new Label
			{
				Text = AppTiles.Letter(name), FontSize = 20, FontAttributes = FontAttributes.Bold,
				TextColor = DesignTheme.ColorOf("OnAccent"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
			},
		};
		var text = new VerticalStackLayout
		{
			Spacing = 1, VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = name, FontAttributes = FontAttributes.Bold, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation },
				new Label { Text = summary, FontSize = 13, TextColor = DesignTheme.ColorOf("Muted"), LineBreakMode = LineBreakMode.TailTruncation },
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
			ColumnSpacing = 12, Padding = new Thickness(16, 10), MinimumHeightRequest = 52,
		};
		grid.Add(tile, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(chevron, 2, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += async (_, _) => await Navigation.PushAsync(page());
		grid.GestureRecognizers.Add(tap);
		AppRows.Add(grid);
	}

	private async void OnAppIconsTapped(object? sender, TappedEventArgs e) =>
		await Navigation.PushAsync(IPlatformApplication.Current!.Services.GetRequiredService<AppIconsPage>());
}
