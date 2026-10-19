using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Settings > Language. The list holds the languages that actually have a translation file, shown by
/// their own endonym where .NET knows one ("Nederlands", not "nl") so it is readable to the person
/// looking for their language.
/// </summary>
public partial class SettingsLanguagePage : ContentPage
{
	private bool _switching;

	public SettingsLanguagePage()
	{
		InitializeComponent();
		var first = true;
		foreach (var code in L10n.AvailableLanguages)
		{
			if (!first)
				Rows.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });
			first = false;
			Rows.Add(LanguageRow(code));
		}
	}

	private View LanguageRow(string code)
	{
		var tick = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M2,7 L7,12 L16,2")!,
			Stroke = new SolidColorBrush(DesignTheme.ColorOf("Accent")), StrokeThickness = 2.4,
			StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, StrokeLineJoin = Microsoft.Maui.Controls.Shapes.PenLineJoin.Round,
			WidthRequest = 18, HeightRequest = 14, VerticalOptions = LayoutOptions.Center,
			IsVisible = code == L10n.CurrentLanguage,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 12), MinimumHeightRequest = 48,
		};
		grid.Add(new Label { Text = L10n.GetDisplayName(code), FontSize = 16, VerticalOptions = LayoutOptions.Center }, 0, 0);
		grid.Add(tick, 1, 0);
		var tap = new TapGestureRecognizer();
		tap.Tapped += async (_, _) => await SwitchToAsync(code);
		grid.GestureRecognizers.Add(tap);
		return grid;
	}

	private async Task SwitchToAsync(string code)
	{
		if (_switching || code == L10n.CurrentLanguage)
			return;
		_switching = true;

		L10n.SetLanguage(code);

		// {l:Localize} resolves while a page is being built, so every page already built still holds
		// the old language. Asking the user to restart didn't help: Android keeps the process alive
		// when the app is closed and reopened, so the same page objects came back. Rebuild the shell
		// instead, and land the user back on the Settings tab so they can see the switch took.
		await Task.Yield();

		var window = Application.Current?.Windows.FirstOrDefault();
		if (window is null)
			return;

		window.Page = new AppShell();
		await Shell.Current.GoToAsync("//settings");
	}
}
