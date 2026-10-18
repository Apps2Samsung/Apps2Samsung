using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the TV sheet hands back: a found TV by index, "enter an IP", or "rescan".</summary>
public sealed record TvSheetChoice(int Index, bool Manual, bool Rescan);

/// <summary>
/// The TV sheet: the found TVs with their LED colours, a row for a TV the scan didn't find, and
/// Rescan. The Install page pushes it modally and awaits <see cref="Result"/> (null = closed).
/// </summary>
public partial class TvSheetPage : ContentPage
{
	private readonly TaskCompletionSource<TvSheetChoice?> _result = new();
	private bool _closedWithResult;

	public Task<TvSheetChoice?> Result => _result.Task;

	public TvSheetPage(IReadOnlyList<string> ips, IReadOnlyList<string> labels, IReadOnlyList<NetworkDevice?> devices, int selected)
	{
		InitializeComponent();
		for (var i = 0; i < ips.Count; i++)
		{
			if (i > 0)
				Rows.Add(Hairline());
			var device = i < devices.Count ? devices[i] : null;
			var name = string.IsNullOrWhiteSpace(device?.DeviceName) ? L10n.Get("tvCardSamsungTv") : device!.DeviceName!;
			var subline = string.IsNullOrWhiteSpace(device?.ModelName) ? ips[i] : $"{device!.ModelName} · {ips[i]}";
			var index = i;
			Rows.Add(TvRow(name, subline, i == selected, device?.DebugPortOpen ?? true, () => Choose(new TvSheetChoice(index, false, false))));
		}
		if (ips.Count > 0)
			Rows.Add(Hairline());
		Rows.Add(ManualRow());
	}

	private static Color C(string token) => DesignTheme.ColorOf(token);
	private static BoxView Hairline() => new() { HeightRequest = 1, Margin = new Thickness(16, 0, 0, 0), Color = C("Hair") };

	private View TvRow(string name, string subline, bool selected, bool ready, Action tap)
	{
		var radio = new Border
		{
			WidthRequest = 22, HeightRequest = 22, StrokeThickness = 2, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			Stroke = new SolidColorBrush(C(selected ? "Accent" : "Hair")),
			BackgroundColor = selected ? C("Accent") : Colors.Transparent,
		};
		var text = new VerticalStackLayout
		{
			Spacing = 1, VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = name, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation },
				new Label { Text = subline, FontSize = 13, TextColor = C("Muted"), LineBreakMode = LineBreakMode.TailTruncation },
			},
		};
		var dot = new BoxView { WidthRequest = 10, HeightRequest = 10, CornerRadius = 5, VerticalOptions = LayoutOptions.Center, Color = C(ready ? "Ok" : "Warn") };
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 52,
		};
		grid.Add(radio, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(dot, 2, 0);
		var gesture = new TapGestureRecognizer();
		gesture.Tapped += (_, _) => tap();
		grid.GestureRecognizers.Add(gesture);
		return grid;
	}

	private View ManualRow()
	{
		var tile = new Border
		{
			WidthRequest = 44, HeightRequest = 44, StrokeThickness = 0, BackgroundColor = C("Fill"),
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 11 },
			Content = new Label { Text = "✏️", FontSize = 20, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
		};
		var text = new VerticalStackLayout
		{
			Spacing = 1, VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = L10n.Get("lblEnterIpManually"), FontAttributes = FontAttributes.Bold },
				new Label { Text = L10n.Get("lblEnterIpHint"), FontSize = 13, TextColor = C("Muted") },
			},
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(44), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 52,
		};
		grid.Add(tile, 0, 0);
		grid.Add(text, 1, 0);
		var gesture = new TapGestureRecognizer();
		gesture.Tapped += (_, _) => Choose(new TvSheetChoice(-1, true, false));
		grid.GestureRecognizers.Add(gesture);
		return grid;
	}

	private async void Choose(TvSheetChoice choice)
	{
		if (_closedWithResult)
			return;
		_closedWithResult = true;
		_result.TrySetResult(choice);
		await Navigation.PopModalAsync();
	}

	private void OnRescanClicked(object? sender, EventArgs e) => Choose(new TvSheetChoice(-1, false, true));

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_closedWithResult)
			_result.TrySetResult(null);
	}
}
