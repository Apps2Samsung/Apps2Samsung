using Apps2Samsung.Mobile.Resources.Styles;
using Microsoft.Maui.Controls.Shapes;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>One row of the ⋯ menu: its text, a 24×24 stroke icon (path data) and whether it is destructive.</summary>
public sealed record MoreMenuItem(string Label, string Icon, bool Destructive = false);

/// <summary>
/// The ⋯ menu of the top bar (#755): a card dropped under the button with an icon per row, over a
/// light scrim that closes it. It replaces the system action sheet (with its Cancel button), which
/// looked out of place next to the rest of the app. Shown as a transparent modal page so it sits
/// above the tab bar; <see cref="ShowAsync"/> returns the tapped row's index, or -1.
/// </summary>
public sealed class MoreMenu : ContentPage
{
	public const string IconRescan = "M20,12 A8,8 0 1 1 17.7,6.4 M20,4 L20,8.5 L15.5,8.5";
	public const string IconAdd = "M12,5 L12,19 M5,12 L19,12";
	public const string IconInfo = "M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12 M12,11 L12,17 M12,7.5 L12,7.6";
	public const string IconToolbox = "M3,8 L21,8 L21,20 L3,20 Z M8,8 L8,5 L16,5 L16,8 M3,13 L21,13";
	public const string IconDelete = "M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M6,7 L7,20 L17,20 L18,7";

	private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private bool _closing;

	private MoreMenu(IReadOnlyList<MoreMenuItem> items)
	{
		Shell.SetPresentationMode(this, PresentationMode.ModalNotAnimated);
		BackgroundColor = Color.FromRgba(0, 0, 0, 0.12);

		var rows = new VerticalStackLayout();
		for (var i = 0; i < items.Count; i++)
		{
			if (i > 0)
				rows.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(48, 0, 0, 0), Color = C("Hair") });
			rows.Add(Row(items[i], i));
		}

		var card = new Border
		{
			WidthRequest = 248,
			HorizontalOptions = LayoutOptions.End,
			VerticalOptions = LayoutOptions.Start,
			// Under the ⋯ button: the top bar is 56 high with 8 of padding on the right.
			Margin = new Thickness(0, 52, 12, 0),
			StrokeThickness = 0,
			StrokeShape = new RoundRectangle { CornerRadius = 14 },
			BackgroundColor = C("Surface"),
			Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.2f, Radius = 24, Offset = new Point(0, 8) },
			Content = rows,
		};

		var scrim = new Grid();
		scrim.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(-1)) });
		Content = new Grid { Children = { scrim, card } };
	}

	public static async Task<int> ShowAsync(Page page, params MoreMenuItem[] items)
	{
		var menu = new MoreMenu(items);
		await page.Navigation.PushModalAsync(menu, animated: false);
		return await menu._result.Task;
	}

	private static Color C(string token) => DesignTheme.ColorOf(token);

	private View Row(MoreMenuItem item, int index)
	{
		var color = C(item.Destructive ? "Bad" : "Ink");
		var icon = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(item.Icon)!,
			Stroke = new SolidColorBrush(item.Destructive ? color : C("Accent")),
			StrokeThickness = 2,
			StrokeLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round,
			WidthRequest = 18,
			HeightRequest = 18,
			Aspect = Stretch.Uniform,
			VerticalOptions = LayoutOptions.Center,
		};
		var label = new Label { Text = item.Label, FontSize = 16, TextColor = color, VerticalOptions = LayoutOptions.Center };
		Grid.SetColumn(label, 1);
		var row = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 12,
			Padding = new Thickness(14, 0, 16, 0),
			MinimumHeightRequest = 48,
			Children = { icon, label },
		};
		row.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Close(index)) });
		SemanticProperties.SetDescription(row, item.Label);
		return row;
	}

	// The menu is gone before the caller hears the choice. Handing it over first ran the caller
	// (push TV information, open a prompt) while this modal was still being dismissed, and on
	// Android that froze the app (#818).
	private async void Close(int index)
	{
		if (_closing)
			return;
		_closing = true;
		try
		{
			await Navigation.PopModalAsync(animated: false);
		}
		finally
		{
			_result.TrySetResult(index);
		}
	}

	// The system back button closes the menu like a tap on the scrim.
	protected override bool OnBackButtonPressed()
	{
		Close(-1);
		return true;
	}
}
