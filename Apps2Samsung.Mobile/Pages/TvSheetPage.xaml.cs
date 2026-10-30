using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the TV sheet hands back: the index of the TV to switch to.</summary>
public sealed record TvSheetChoice(int Index);

/// <summary>
/// The Switch TV sheet: the TVs in <see cref="TvSelection"/> with their LED colours and the one in
/// use ticked. Only offered once there is more than one TV; Rescan and Add TV by IP live in the ⋯ menu.
/// <see cref="TvPicker"/> pushes it modally and awaits <see cref="Result"/>
/// (null = closed).
/// </summary>
public partial class TvSheetPage : ContentPage
{
	private readonly TaskCompletionSource<TvSheetChoice?> _result = new();
	private bool _closedWithResult;

	public Task<TvSheetChoice?> Result => _result.Task;

	public TvSheetPage(IReadOnlyList<TvEntry> tvs, int selected)
	{
		InitializeComponent();
		for (var i = 0; i < tvs.Count; i++)
		{
			if (i > 0)
				Rows.Add(Hairline(37));
			var tv = tvs[i];
			var localIp = NetworkInfo.LocalIpFor(tv.Ip);
			var state = TvCardStates.ForDevice(tv.Device ?? new NetworkDevice { IpAddress = tv.Ip }, localIp, L10n.Get);
			var index = i;
			Rows.Add(TvRow(state.Name, $"{state.Subline} · {state.Message}", i == selected, state.Led == TvLed.Ok,
				() => Choose(new TvSheetChoice(index))));
		}
		FoundTitle.IsVisible = FoundCard.IsVisible = tvs.Count > 0;
	}

	private static Color C(string token) => DesignTheme.ColorOf(token);
	private static BoxView Hairline(double inset) => new() { HeightRequest = 1, Margin = new Thickness(inset, 0, 0, 0), Color = C("Hair") };

	// LED dot, name over "model · IP · status", a tick on the TV in use.
	private View TvRow(string name, string subline, bool selected, bool ready, Action tap)
	{
		var dot = new BoxView { WidthRequest = 9, HeightRequest = 9, CornerRadius = 5, VerticalOptions = LayoutOptions.Center, Color = C(ready ? "Ok" : "Warn") };
		var text = new VerticalStackLayout
		{
			Spacing = 1, VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label { Text = name, FontAttributes = FontAttributes.Bold, FontSize = 15, LineBreakMode = LineBreakMode.TailTruncation },
				new Label { Text = subline, FontSize = 13, TextColor = C("Muted"), LineBreakMode = LineBreakMode.TailTruncation },
			},
		};
		var tick = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = ParsePath("M2,7 L7,12 L16,2"), Stroke = new SolidColorBrush(C("Accent")), StrokeThickness = 2.4,
			StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, StrokeLineJoin = Microsoft.Maui.Controls.Shapes.PenLineJoin.Round,
			WidthRequest = 18, HeightRequest = 14, VerticalOptions = LayoutOptions.Center, IsVisible = selected,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
			ColumnSpacing = 12, Padding = new Thickness(16, 11), MinimumHeightRequest = 52,
		};
		grid.Add(dot, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(tick, 2, 0);
		var gesture = new TapGestureRecognizer();
		gesture.Tapped += (_, _) => tap();
		grid.GestureRecognizers.Add(gesture);
		return grid;
	}

	private static Microsoft.Maui.Controls.Shapes.Geometry ParsePath(string data) =>
		(Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString(data)!;

	private async void Choose(TvSheetChoice choice)
	{
		if (_closedWithResult)
			return;
		_closedWithResult = true;
		_result.TrySetResult(choice);
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
