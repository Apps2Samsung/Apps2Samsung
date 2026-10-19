using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The install progress sheet. The Install tab pushes it modally once the pre-install guards have
/// passed, then feeds it: <see cref="SetStep"/> as the install moves on, <see cref="Report"/> with
/// every status line, <see cref="Fail"/> when it goes wrong. Swiping it away (Back) leaves the
/// install running; the TV card keeps showing its status. Main thread only.
/// </summary>
public partial class InstallProgressPage : ContentPage
{
	private sealed record StepRow(InstallStep Step, Border Badge, Label Number, Microsoft.Maui.Controls.Shapes.Path Tick, Label Title, Label Detail);

	private readonly List<StepRow> _rows = new();
	private readonly List<string> _log = new();
	private InstallStep _current = InstallStep.Download;
	private bool _failed;

	/// <param name="opensAfter">Whether the fifth step (Open the app) happens at all.</param>
	public InstallProgressPage(string appTitle, string? category, string tvName, bool opensAfter)
	{
		InitializeComponent();
		TitleLabel.Text = string.Format(L10n.Get("lblInstallingApp"), appTitle);
		SubLabel.Text = string.Format(L10n.Get("lblOnTvName"), tvName);
		Tile.BackgroundColor = DesignTheme.ColorOf(AppTiles.ColorToken(category, appTitle));
		TileLetter.Text = AppTiles.Letter(appTitle);

		var steps = new List<(InstallStep step, string key)>
		{
			(InstallStep.Download, "lblStepDownload"),
			(InstallStep.Certificate, "lblStepCertificate"),
			(InstallStep.Sign, "lblStepSign"),
			(InstallStep.Install, "lblStepInstall"),
		};
		if (opensAfter)
			steps.Add((InstallStep.Open, "lblStepOpen"));

		for (var i = 0; i < steps.Count; i++)
		{
			if (i > 0)
				Steps.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(56, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });
			Steps.Add(BuildRow(steps[i].step, i + 1, L10n.Get(steps[i].key)));
		}
		Render(null);
	}

	/// <summary>The install reached <paramref name="step"/>; <paramref name="percent"/> is the TV's own
	/// install percentage while it reports one.</summary>
	public void SetStep(InstallStep step, int? percent = null)
	{
		if (_failed)
			return;
		_current = step;
		Render(percent);
	}

	/// <summary>A status line: the current step's detail, and a line in the log.</summary>
	public void Report(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;
		_log.Add(message);
		LogLabel.Text = string.Join(Environment.NewLine, _log);
		if (!_failed && _rows.FirstOrDefault(r => r.Step == _current) is { } row)
			row.Detail.Text = message;
	}

	/// <summary>The install failed: the current step turns red with the reason, and Close appears.</summary>
	public void Fail(string message)
	{
		_failed = true;
		Report(message);
		if (_rows.FirstOrDefault(r => r.Step == _current) is { } row)
		{
			row.Badge.BackgroundColor = DesignTheme.ColorOf("BadSoft");
			row.Number.TextColor = DesignTheme.ColorOf("Bad");
			row.Detail.Text = message;
			row.Detail.TextColor = DesignTheme.ColorOf("Bad");
			row.Detail.IsVisible = true;
		}
		Bar.ProgressColor = DesignTheme.ColorOf("Bad");
		CloseBtn.IsVisible = true;
	}

	/// <summary>Closes the sheet if it is still the page on top (the user may have swiped it away).</summary>
	public async Task CloseAsync()
	{
		if (ReferenceEquals(Navigation.ModalStack.LastOrDefault(), this))
			await Navigation.PopModalAsync();
	}

	private View BuildRow(InstallStep step, int number, string title)
	{
		var numberLabel = new Label
		{
			Text = number.ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold,
			HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
		};
		var tick = new Microsoft.Maui.Controls.Shapes.Path
		{
			Data = (Microsoft.Maui.Controls.Shapes.Geometry)new Microsoft.Maui.Controls.Shapes.PathGeometryConverter().ConvertFromInvariantString("M2,7 L7,12 L16,2")!,
			Stroke = new SolidColorBrush(DesignTheme.ColorOf("Ok")), StrokeThickness = 3,
			StrokeLineCap = Microsoft.Maui.Controls.Shapes.PenLineCap.Round, StrokeLineJoin = Microsoft.Maui.Controls.Shapes.PenLineJoin.Round,
			WidthRequest = 14, HeightRequest = 11, Aspect = Stretch.Uniform,
			HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
		};
		var badgeContent = new Grid();
		badgeContent.Add(numberLabel);
		badgeContent.Add(tick);
		var badge = new Border
		{
			WidthRequest = 28, HeightRequest = 28, StrokeThickness = 0, VerticalOptions = LayoutOptions.Center,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
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
		_rows.Add(new StepRow(step, badge, numberLabel, tick, titleLabel, detail));
		return grid;
	}

	// Done steps ticked, the current one in accent with its detail line, the rest numbered and muted.
	private void Render(int? percent)
	{
		var index = _rows.FindIndex(r => r.Step == _current);
		for (var i = 0; i < _rows.Count; i++)
		{
			var row = _rows[i];
			var done = i < index;
			var active = i == index;
			row.Tick.IsVisible = done;
			row.Number.IsVisible = !done;
			row.Badge.BackgroundColor = DesignTheme.ColorOf(done ? "OkSoft" : active ? "Accent" : "AccentSoft");
			row.Number.TextColor = DesignTheme.ColorOf(active ? "OnAccent" : "Accent");
			row.Title.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
			row.Title.TextColor = DesignTheme.ColorOf(i > index ? "Muted" : "Ink");
			row.Detail.IsVisible = active && !string.IsNullOrEmpty(row.Detail.Text);
		}

		var count = Math.Max(1, _rows.Count);
		var within = percent is { } p ? Math.Clamp(p, 0, 100) / 100.0 : 0.5;
		var fraction = Math.Clamp((Math.Max(0, index) + within) / count, 0, 1);
		Bar.Progress = fraction;
		StepLabel.Text = string.Format(L10n.Get("lblInstallStepOf"), Math.Max(1, index + 1), count);
		PercentLabel.Text = $"{(int)Math.Round(fraction * 100)}%";
	}

	private void OnLogClicked(object? sender, EventArgs e)
	{
		LogCard.IsVisible = !LogCard.IsVisible;
		LogBtn.Text = L10n.Get(LogCard.IsVisible ? "lblHideLog" : "lblShowLog");
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();
}
