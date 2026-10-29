using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the user chose for an installed app.</summary>
public enum AppAction { Launch, Stop, Debug, Uninstall, ViewInCatalog }

/// <summary>The per-app sheet: the app, then Launch / Stop / Debug console / Uninstall as rows,
/// and View in catalog for an app the catalog lists (#888).
/// <see cref="Result"/> completes with the choice, or null when closed.</summary>
public partial class AppActionsSheetPage : ContentPage
{
	private readonly TaskCompletionSource<AppAction?> _result = new();
	private bool _chosen;

	public Task<AppAction?> Result => _result.Task;

	public AppActionsSheetPage(InstalledApp app, bool inCatalog = false)
	{
		InitializeComponent();
		TitleLabel.Text = app.DisplayName;
		SubLabel.Text = $"v{app.Version} · {app.SizeDisplay}";
		IdLabel.Text = app.TizenId;
		if (app.HasIcon)
		{
			IconImage.Source = app.IconUrl;
			IconImage.IsVisible = true;
			LetterTile.IsVisible = false;
		}
		else
		{
			LetterLabel.Text = app.Initials;
			LetterTile.BackgroundColor = Color.FromArgb(app.FallbackColor);
		}

		Actions.Add(Row(L10n.Get("lblRun"), "M8,5.14V19.14L19,12.14L8,5.14Z", "Ink", AppAction.Launch));
		Actions.Add(Hairline());
		Actions.Add(Row(L10n.Get("keyStop"), "M18,18H6V6H18V18Z", "Ink", AppAction.Stop));
		Actions.Add(Hairline());
		Actions.Add(Row(L10n.Get("lblDebugConsole"), "M14,12H10V10H14M14,16H10V14H14M20,8H17.19C16.74,7.22 16.12,6.55 15.37,6.04L17,4.41L15.59,3L13.42,5.17C12.96,5.06 12.5,5 12,5C11.5,5 11.04,5.06 10.59,5.17L8.41,3L7,4.41L8.62,6.04C7.88,6.55 7.26,7.22 6.81,8H4V10H6.09C6.04,10.33 6,10.66 6,11V12H4V14H6V15C6,15.34 6.04,15.67 6.09,16H4V18H6.81C7.85,19.79 9.78,21 12,21C14.22,21 16.15,19.79 17.19,18H20V16H17.91C17.96,15.67 18,15.34 18,15V14H20V12H18V11C18,10.66 17.96,10.33 17.91,10H20V8Z", "Ink", AppAction.Debug));
		if (app.IsRemovable)
		{
			Actions.Add(Hairline());
			Actions.Add(Row(L10n.Get("lblUninstall"), "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z", "Bad", AppAction.Uninstall));
		}
		if (inCatalog)
		{
			// Back to the app's catalog sheet: its description, versions and variants (#888).
			Actions.Insert(0, Hairline());
			Actions.Insert(0, Row(L10n.Get("lblViewInCatalog"), "M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z", "Ink", AppAction.ViewInCatalog));
		}
	}

	private static BoxView Hairline() => new() { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") };

	private View Row(string text, string pathData, string colorToken, AppAction action)
	{
		var color = DesignTheme.ColorOf(colorToken);
		var icon = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString(pathData)!,
			Fill = new SolidColorBrush(color), Aspect = Stretch.Uniform, WidthRequest = 18, HeightRequest = 18,
			HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
		};
		var label = new Label { Text = text, FontSize = 16, TextColor = color, VerticalOptions = LayoutOptions.Center };
		var chevron = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M1,1 L7,6.5 L1,12")!,
			Stroke = new SolidColorBrush(DesignTheme.ColorOf("Ink")), StrokeThickness = 2, Opacity = 0.35,
			WidthRequest = 8, HeightRequest = 13, VerticalOptions = LayoutOptions.Center,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(28), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 13), MinimumHeightRequest = 52,
		};
		grid.Add(icon, 0, 0);
		grid.Add(label, 1, 0);
		grid.Add(chevron, 2, 0);
		var tap = new TapGestureRecognizer();
		// Pop first, then hand back the choice (like MoreMenu, #818): the caller's continuation runs
		// inline on TrySetResult, and View in catalog pushes the next modal from it. With the result
		// set first that push raced this sheet's pop and crashed the app (#888).
		tap.Tapped += async (_, _) =>
		{
			if (_chosen) return;
			_chosen = true;
			try
			{
				await Navigation.PopModalAsync();
			}
			finally
			{
				_result.TrySetResult(action);
			}
		};
		grid.GestureRecognizers.Add(tap);
		return grid;
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_chosen)
			_result.TrySetResult(null);
	}
}
