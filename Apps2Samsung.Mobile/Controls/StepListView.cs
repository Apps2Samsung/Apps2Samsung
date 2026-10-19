using Apps2Samsung.Mobile.Resources.Styles;
using Microsoft.Maui.Controls.Shapes;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// The numbered steps of a sheet (#755): done steps ticked, the current one in accent with a detail
/// line, the rest numbered and muted, a failed one red. Shared by the install progress sheet and the
/// certificate sheet. Main thread only.
/// </summary>
public sealed class StepListView : VerticalStackLayout
{
	private sealed record Row(Border Badge, Label Number, Microsoft.Maui.Controls.Shapes.Path Tick, Label Title, Label Detail);

	private readonly List<Row> _rows = new();
	private int _current;
	private int _failed = -1;

	/// <summary>Whether a done step keeps its detail line (the certificate sheet's "Signed in"); the
	/// install sheet only shows the current step's.</summary>
	public bool KeepDoneDetails { get; set; }

	public int Count => _rows.Count;

	/// <summary>The current step's index; <see cref="Count"/> = all done.</summary>
	public int Current => _current;

	/// <summary>Adds a step and returns its index.</summary>
	public int Add(string title)
	{
		if (_rows.Count > 0)
			Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(56, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });

		var number = new Label
		{
			Text = (_rows.Count + 1).ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold,
			HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
		};
		var tick = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M2,7 L7,12 L16,2")!,
			Stroke = new SolidColorBrush(DesignTheme.ColorOf("Ok")), StrokeThickness = 3,
			StrokeLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
			WidthRequest = 14, HeightRequest = 11, Aspect = Stretch.Uniform,
			HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
		};
		var badgeContent = new Grid();
		badgeContent.Add(number);
		badgeContent.Add(tick);
		var badge = new Border
		{
			WidthRequest = 28, HeightRequest = 28, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new RoundRectangle { CornerRadius = 14 },
			Content = badgeContent,
		};
		var titleLabel = new Label { Text = title, FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };
		var detail = new Label { FontSize = 12, TextColor = DesignTheme.ColorOf("Muted"), LineBreakMode = LineBreakMode.WordWrap };
		var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { titleLabel, detail } };

		var grid = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(28), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 12, Padding = new Thickness(16, 12), MinimumHeightRequest = 52,
		};
		grid.Add(badge, 0, 0);
		grid.Add(text, 1, 0);
		Children.Add(grid);
		_rows.Add(new Row(badge, number, tick, titleLabel, detail));
		Render();
		return _rows.Count - 1;
	}

	/// <summary>Moves to step <paramref name="index"/> (clamped; <see cref="Count"/> ticks them all).</summary>
	public void SetCurrent(int index)
	{
		if (_failed >= 0)
			return;
		_current = Math.Clamp(index, 0, _rows.Count);
		Render();
	}

	/// <summary>The detail line under step <paramref name="index"/>.</summary>
	public void SetDetail(int index, string? text)
	{
		if (index < 0 || index >= _rows.Count)
			return;
		_rows[index].Detail.Text = text;
		Render();
	}

	/// <summary>The current step failed: it turns red with <paramref name="message"/>.</summary>
	public void Fail(string message)
	{
		if (_rows.Count == 0)
			return;
		_failed = Math.Min(_current, _rows.Count - 1);
		var row = _rows[_failed];
		row.Badge.BackgroundColor = DesignTheme.ColorOf("BadSoft");
		row.Number.TextColor = DesignTheme.ColorOf("Bad");
		row.Detail.Text = message;
		row.Detail.TextColor = DesignTheme.ColorOf("Bad");
		row.Detail.IsVisible = true;
	}

	private void Render()
	{
		if (_failed >= 0)
			return;
		for (var i = 0; i < _rows.Count; i++)
		{
			var row = _rows[i];
			var done = i < _current;
			var active = i == _current;
			row.Tick.IsVisible = done;
			row.Number.IsVisible = !done;
			row.Badge.BackgroundColor = DesignTheme.ColorOf(done ? "OkSoft" : active ? "Accent" : "AccentSoft");
			row.Number.TextColor = DesignTheme.ColorOf(active ? "OnAccent" : "Accent");
			row.Title.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
			row.Title.TextColor = DesignTheme.ColorOf(i > _current ? "Muted" : "Ink");
			row.Detail.IsVisible = !string.IsNullOrEmpty(row.Detail.Text) && (active || (done && KeepDoneDetails));
		}
	}
}
