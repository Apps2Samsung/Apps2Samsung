using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Models;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>What the TV sheet hands back: the index of the TV to switch to, and whether its fix was asked for
/// (the button under a TV that needs attention), which opens the Developer Mode guide for it.</summary>
public sealed record TvSheetChoice(int Index, bool Fix = false);

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

	/// <param name="localIps">This phone's addresses, for the readiness verdict per row (the card's own, <see cref="TvCardStates.RowStatus"/>).</param>
	public TvSheetPage(IReadOnlyList<TvEntry> tvs, int selected, IReadOnlyCollection<string>? localIps = null)
	{
		InitializeComponent();
		localIps ??= Array.Empty<string>();
		var ready = 0;
		var attention = 0;
		for (var i = 0; i < tvs.Count; i++)
		{
			var tv = tvs[i];
			var localIp = NetworkInfo.LocalIpFor(tv.Ip);
			var device = tv.Device ?? new NetworkDevice { IpAddress = tv.Ip };
			var state = TvCardStates.ForDevice(device, localIps, localIp, L10n.Get);
			var (led, detail) = TvCardStates.RowStatus(device, localIps, localIp, L10n.Get);
			var index = i;
			if (led == TvLed.Ok)
			{
				// Name over "model · IP", with "via relay" where that is how it is reached.
				if (ready++ > 0)
					Rows.Add(Hairline(37));
				Rows.Add(TvRow(state.Name, string.IsNullOrEmpty(detail) ? state.Subline : $"{state.Subline} · {detail}", i == selected, true,
					() => Choose(new TvSheetChoice(index))));
				continue;
			}

			// A TV that refuses this phone: the card's own sentence under it, and the way to the fix.
			if (attention++ > 0)
				AttentionRows.Add(Hairline(16));
			var block = new VerticalStackLayout { Spacing = 0 };
			block.Add(TvRow(state.Name, state.Subline, i == selected, false, () => Choose(new TvSheetChoice(index))));
			block.Add(new Label
			{
				Text = state.Message, FontSize = 14, LineBreakMode = LineBreakMode.WordWrap,
				Margin = new Thickness(37, 0, 16, 12),
			});
			var fixes = state.Actions.Where(a => a is TvCardAction.FixDeveloperMode or TvCardAction.TurnOnRelay).ToList();
			if (fixes.Count > 0)
			{
				var fix = fixes[0];
				var button = new Button
				{
					Text = L10n.Get(TvCardStates.ActionLabelKey(fix)), FontSize = 14, FontAttributes = FontAttributes.Bold,
					HeightRequest = 44, MinimumHeightRequest = 44, CornerRadius = 12, Padding = new Thickness(16, 0),
					HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(37, 0, 16, 14),
					BackgroundColor = C("WarnSoft"), TextColor = C("Ink"),
				};
				button.Clicked += (_, _) => Choose(new TvSheetChoice(index, Fix: true));
				block.Add(button);
			}
			AttentionRows.Add(block);
		}

		FoundTitle.IsVisible = FoundCard.IsVisible = ready > 0;
		AttentionTitle.IsVisible = AttentionCard.IsVisible = attention > 0;
		if (attention > 0)
		{
			FoundTitle.Text = L10n.Get("lblTvListReady");
			AttentionTitle.Text = string.Format(L10n.Get("lblTvListNeedsAttention"), attention);
		}
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
		// Pop first, then hand back the choice (like the app sheet, #888): the caller may open the
		// Developer Mode guide from it, and a modal pushed while this one is still closing crashed.
		try
		{
			await Navigation.PopModalAsync();
		}
		finally
		{
			_result.TrySetResult(choice);
		}
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_closedWithResult)
			_result.TrySetResult(null);
	}
}
