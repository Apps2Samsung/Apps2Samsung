using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Resources.Styles;

namespace Apps2Samsung.Mobile.Controls;

/// <summary>
/// Paints a <see cref="TvCardState"/>. <see cref="Actions"/> is whatever the page wants on the
/// screen under the status line: chip buttons, usually. The "power on" animation plays when a TV
/// first appears or a different TV takes the screen; the LED pulses while the state is busy.
/// </summary>
public partial class TvCardView : ContentView
{
	public static readonly BindableProperty StateProperty = BindableProperty.Create(
		nameof(State), typeof(TvCardState), typeof(TvCardView), null,
		propertyChanged: (b, _, _) => ((TvCardView)b).Render());

	private string? _shownDeviceName;
	private int _pulseGeneration;

	public TvCardView()
	{
		InitializeComponent();
		// The sheen is the same in both themes, so reading it once is enough.
		SheenStart.Color = DesignTheme.ColorOf("GlassSheen");
		SheenEnd.Color = DesignTheme.ColorOf("GlassSheenFade");
		var tap = new TapGestureRecognizer();
		tap.Tapped += (_, _) => Tapped?.Invoke(this, EventArgs.Empty);
		GestureRecognizers.Add(tap);
	}

	/// <summary>The card itself was tapped (not one of the <see cref="Actions"/>).</summary>
	public event EventHandler? Tapped;

	public TvCardState? State
	{
		get => (TvCardState?)GetValue(StateProperty);
		set => SetValue(StateProperty, value);
	}

	/// <summary>The content under the status line, on the screen.</summary>
	public View? Actions
	{
		get => ActionsHost.Content;
		set => ActionsHost.Content = value;
	}

	private void Render()
	{
		var state = State;
		NameLabel.Text = state?.Name ?? string.Empty;
		SubLabel.Text = state?.Subline ?? string.Empty;
		SubLabel.IsVisible = !string.IsNullOrEmpty(SubLabel.Text);
		MessageLabel.Text = state?.Message ?? string.Empty;
		StateRow.IsVisible = !string.IsNullOrEmpty(MessageLabel.Text);

		var (token, blur) = state?.Led switch
		{
			TvLed.Ok => ("LedOk", 10f),
			TvLed.Warn => ("LedWarn", 10f),
			TvLed.Bad => ("LedBad", 8f),
			TvLed.Busy => ("LedBusy", 0f),
			_ => ("GlassMuted", 0f),
		};
		var color = DesignTheme.ColorOf(token);
		Led.Fill = new SolidColorBrush(color);
		Led.Shadow = blur > 0 ? new Shadow { Brush = new SolidColorBrush(color), Radius = blur, Opacity = 1, Offset = Point.Zero } : null;
		Pulse(state?.Led == TvLed.Busy);

		Progress.IsVisible = state?.Progress is not null;
		Progress.Progress = (state?.Progress ?? 0) / 100.0;

		// A TV taking the screen (first find, or a different one picked) gets the power-on.
		var deviceName = state is { IsDevice: true } ? state.Name : null;
		if (deviceName is not null && deviceName != _shownDeviceName)
			_ = PowerOnAsync();
		_shownDeviceName = deviceName;
	}

	// Accent LED breathing at 1.2 s while something runs; the generation counter ends the loop
	// of an earlier state without a race. Each half-cycle is paced by a delay as well as the fade:
	// with the phone's animations off (Developer options animator scale 0, Accessibility "Remove
	// animations") FadeTo completes at once, and the loop alone would spin the UI thread at 100 %
	// until Android reports the app as not responding (#899).
	private async void Pulse(bool on)
	{
		var generation = ++_pulseGeneration;
		if (!on)
		{
			Led.Opacity = 1;
			return;
		}
		while (generation == _pulseGeneration)
		{
			await Task.WhenAll(Led.FadeTo(0.35, 600, Easing.SinInOut), Task.Delay(600));
			if (generation != _pulseGeneration) break;
			await Task.WhenAll(Led.FadeTo(1, 600, Easing.SinInOut), Task.Delay(600));
		}
		Led.Opacity = 1;
	}

	// The picture grows from a line with a slight overshoot: scale-Y 4 % → 102 % → 100 % in 700 ms.
	private async Task PowerOnAsync()
	{
		Screen.Opacity = 0;
		Screen.ScaleY = 0.04;
		await Task.WhenAll(Screen.FadeTo(1, 420, Easing.CubicOut), Screen.ScaleYTo(1.02, 420, Easing.CubicOut));
		await Screen.ScaleYTo(1, 280, Easing.CubicOut);
	}
}
