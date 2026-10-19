using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Resources.Styles;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>What the top bar's status dot says.</summary>
public enum TopBarStatus { None, Ok, Warn, Bad, Busy }

/// <summary>The button on the right of the top bar.</summary>
public enum TopBarAction { None, More, Power, Glyph }

/// <summary>
/// The top bar (#755, option C). A page sets <see cref="Title"/>, <see cref="Subtitle"/> and
/// <see cref="Status"/>; <see cref="ShowPicker"/> adds the ▾ and makes the title open the TV sheet
/// (<see cref="TitleTapped"/>). <see cref="BackText"/> turns the left side into Back (popping the
/// page unless <see cref="BackTapped"/> is handled), <see cref="ShowLogo"/> puts the app logo there.
/// </summary>
public partial class TopBarView : ContentView
{
	public static readonly BindableProperty TitleProperty = BindableProperty.Create(
		nameof(Title), typeof(string), typeof(TopBarView), string.Empty, propertyChanged: Rerender);
	public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
		nameof(Subtitle), typeof(string), typeof(TopBarView), string.Empty, propertyChanged: Rerender);
	public static readonly BindableProperty StatusProperty = BindableProperty.Create(
		nameof(Status), typeof(TopBarStatus), typeof(TopBarView), TopBarStatus.None, propertyChanged: Rerender);
	public static readonly BindableProperty ShowPickerProperty = BindableProperty.Create(
		nameof(ShowPicker), typeof(bool), typeof(TopBarView), false, propertyChanged: Rerender);
	public static readonly BindableProperty ShowLogoProperty = BindableProperty.Create(
		nameof(ShowLogo), typeof(bool), typeof(TopBarView), false, propertyChanged: Rerender);
	public static readonly BindableProperty BackTextProperty = BindableProperty.Create(
		nameof(BackText), typeof(string), typeof(TopBarView), string.Empty, propertyChanged: Rerender);
	public static readonly BindableProperty GlyphProperty = BindableProperty.Create(
		nameof(Glyph), typeof(string), typeof(TopBarView), string.Empty, propertyChanged: Rerender);
	public static readonly BindableProperty ActionProperty = BindableProperty.Create(
		nameof(Action), typeof(TopBarAction), typeof(TopBarView), TopBarAction.None, propertyChanged: Rerender);

	public TopBarView()
	{
		InitializeComponent();
		Render();
	}

	public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
	public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
	public TopBarStatus Status { get => (TopBarStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
	public bool ShowPicker { get => (bool)GetValue(ShowPickerProperty); set => SetValue(ShowPickerProperty, value); }
	public bool ShowLogo { get => (bool)GetValue(ShowLogoProperty); set => SetValue(ShowLogoProperty, value); }
	public string BackText { get => (string)GetValue(BackTextProperty); set => SetValue(BackTextProperty, value); }
	public TopBarAction Action { get => (TopBarAction)GetValue(ActionProperty); set => SetValue(ActionProperty, value); }
	/// <summary>The text of the button on the right when <see cref="Action"/> is Glyph (⟳, 📤).</summary>
	public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

	/// <summary>The title was tapped while it shows the ▾: open the TV sheet.</summary>
	public event EventHandler? TitleTapped;
	public event EventHandler? MoreTapped;
	public event EventHandler? PowerTapped;
	public event EventHandler? GlyphTapped;
	/// <summary>Back was tapped. Without a handler the page is popped.</summary>
	public event EventHandler? BackTapped;

	/// <summary>Shows the TV the way the TV card does: its name, the status line and the LED colour.</summary>
	public void ShowTv(TvCardState state)
	{
		Title = state.Name;
		Subtitle = state.Message;
		Status = state.Led switch
		{
			TvLed.Ok => TopBarStatus.Ok,
			TvLed.Warn => TopBarStatus.Warn,
			TvLed.Bad => TopBarStatus.Bad,
			TvLed.Busy => TopBarStatus.Busy,
			_ => TopBarStatus.None,
		};
	}

	private static void Rerender(BindableObject bindable, object oldValue, object newValue) => ((TopBarView)bindable).Render();

	private void Render()
	{
		// XAML sets properties before InitializeComponent has run on some paths; nothing to draw yet.
		if (TitleLabel is null)
			return;

		var hasBack = !string.IsNullOrEmpty(BackText);
		BackButton.IsVisible = hasBack;
		BackLabel.Text = BackText;
		Logo.IsVisible = ShowLogo && !hasBack;

		TitleLabel.Text = Title;
		PickerChevron.IsVisible = ShowPicker;

		SubLabel.Text = Subtitle;
		SubRow.IsVisible = !string.IsNullOrEmpty(Subtitle);
		var busy = Status == TopBarStatus.Busy;
		SubSpinner.IsVisible = busy;
		SubSpinner.IsRunning = busy;
		SubDot.IsVisible = Status is TopBarStatus.Ok or TopBarStatus.Warn or TopBarStatus.Bad;
		if (SubDot.IsVisible)
			SubDot.Fill = new SolidColorBrush(DesignTheme.ColorOf(Status switch
			{
				TopBarStatus.Ok => "Ok",
				TopBarStatus.Warn => "Warn",
				_ => "Bad",
			}));

		MoreButton.IsVisible = Action == TopBarAction.More;
		PowerButton.IsVisible = Action == TopBarAction.Power;
		GlyphButton.IsVisible = Action == TopBarAction.Glyph;
		GlyphButton.Text = Glyph;
	}

	private void OnTitleTapped(object? sender, TappedEventArgs e)
	{
		if (ShowPicker)
			TitleTapped?.Invoke(this, EventArgs.Empty);
	}

	private void OnMoreTapped(object? sender, TappedEventArgs e) => MoreTapped?.Invoke(this, EventArgs.Empty);

	private void OnPowerTapped(object? sender, TappedEventArgs e) => PowerTapped?.Invoke(this, EventArgs.Empty);

	private void OnGlyphClicked(object? sender, EventArgs e) => GlyphTapped?.Invoke(this, EventArgs.Empty);

	private async void OnBackTapped(object? sender, TappedEventArgs e)
	{
		if (BackTapped is not null)
		{
			BackTapped.Invoke(this, EventArgs.Empty);
			return;
		}
		if (Navigation.NavigationStack.Count > 1)
			await Navigation.PopAsync();
	}
}
