using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the TV sheet hands back: a found TV by index, "enter an IP", or "rescan".</summary>
public sealed record TvSheetChoice(int Index, bool Manual, bool Rescan);

/// <summary>
/// The TV sheet: the TVs in <see cref="TvSelection"/> with their LED colours, Rescan, and a row for a
/// TV the scan didn't find. <see cref="TvPicker"/> pushes it modally and awaits <see cref="Result"/>
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
		var localIp = NetworkInfo.GetLocalIPv4();
		for (var i = 0; i < tvs.Count; i++)
		{
			if (i > 0)
				Rows.Add(Hairline(37));
			var tv = tvs[i];
			var state = TvCardStates.ForDevice(tv.Device ?? new NetworkDevice { IpAddress = tv.Ip }, localIp, L10n.Get);
			var index = i;
			Rows.Add(TvRow(state.Name, $"{state.Subline} · {state.Message}", i == selected, state.Led == TvLed.Ok,
				() => Choose(new TvSheetChoice(index, false, false))));
		}
		FoundTitle.IsVisible = FoundCard.IsVisible = tvs.Count > 0;

		Commands.Add(CommandRow("M20,12 A8,8 0 1 1 17.7,6.4 M20,4 L20,8.5 L15.5,8.5", L10n.Get("lblRescan"),
			() => Choose(new TvSheetChoice(-1, false, true))));
		Commands.Add(Hairline(46));
		Commands.Add(CommandRow("M12,5 L12,19 M5,12 L19,12", L10n.Get("lblEnterIpManually"),
			() => Choose(new TvSheetChoice(-1, true, false))));
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

	// An accent icon and label: Rescan, Enter an IP address.
	private View CommandRow(string iconPath, string label, Action tap)
	{
		var icon = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = ParsePath(iconPath), Stroke = new SolidColorBrush(C("Accent")), StrokeThickness = 2,
			StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, WidthRequest = 18, HeightRequest = 18, VerticalOptions = LayoutOptions.Center,
			Aspect = Stretch.Uniform,
		};
		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 12, Padding = new Thickness(16, 14), MinimumHeightRequest = 48,
		};
		grid.Add(icon, 0, 0);
		grid.Add(new Label { Text = label, FontSize = 15, TextColor = C("Accent"), VerticalOptions = LayoutOptions.Center }, 1, 0);
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
