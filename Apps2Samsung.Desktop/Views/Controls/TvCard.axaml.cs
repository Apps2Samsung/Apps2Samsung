using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Apps2Samsung.Design;

namespace Apps2Samsung.Views.Controls
{
    /// <summary>
    /// Paints a <see cref="TvCardState"/>. <see cref="Actions"/> is whatever the page wants on the
    /// screen under the status line: chip buttons, usually. The "power on" animation plays when a
    /// TV first appears or a different TV takes the screen.
    /// </summary>
    public partial class TvCard : UserControl
    {
        public static readonly StyledProperty<TvCardState?> StateProperty =
            AvaloniaProperty.Register<TvCard, TvCardState?>(nameof(State));

        public static readonly StyledProperty<object?> ActionsProperty =
            AvaloniaProperty.Register<TvCard, object?>(nameof(Actions));

        private string? _shownDeviceName;

        public TvCard()
        {
            InitializeComponent();
            ActionsHost.Bind(ContentPresenter.ContentProperty, this.GetObservable(ActionsProperty));
            Render();
        }

        public TvCardState? State
        {
            get => GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        public object? Actions
        {
            get => GetValue(ActionsProperty);
            set => SetValue(ActionsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == StateProperty)
                Render();
        }

        private void Render()
        {
            var state = State;
            NameText.Text = state?.Name ?? string.Empty;
            SubText.Text = state?.Subline ?? string.Empty;
            SubText.IsVisible = !string.IsNullOrEmpty(SubText.Text);
            MessageText.Text = state?.Message ?? string.Empty;
            StateRow.IsVisible = !string.IsNullOrEmpty(MessageText.Text);

            Led.Classes.Set("ok", state?.Led == TvLed.Ok);
            Led.Classes.Set("warn", state?.Led == TvLed.Warn);
            Led.Classes.Set("bad", state?.Led == TvLed.Bad);
            Led.Classes.Set("busy", state?.Led == TvLed.Busy);
            Led.BoxShadow = Glow(state?.Led);

            Progress.IsVisible = state?.Progress is not null;
            Progress.Value = state?.Progress ?? 0;

            // A TV taking the screen (first find, or a different one picked) gets the power-on.
            var deviceName = state is { IsDevice: true } ? state.Name : null;
            if (deviceName is not null && deviceName != _shownDeviceName)
                _ = PowerOnAsync();
            _shownDeviceName = deviceName;
        }

        private BoxShadows Glow(TvLed? led)
        {
            var key = led switch
            {
                TvLed.Ok => "LedOkColor",
                TvLed.Warn => "LedWarnColor",
                TvLed.Bad => "LedBadColor",
                _ => null,
            };
            if (key is null || !this.TryFindResource(key, ActualThemeVariant, out var value) || value is not Color color)
                return default;
            return new BoxShadows(new BoxShadow { Blur = led == TvLed.Bad ? 8 : 10, Color = color });
        }

        // "Power on" when a TV appears: the picture grows from a line, with a slight overshoot
        // (scale-Y 4 % → 102 % → 100 % over 700 ms). Built in code so it can be awaited and never
        // leaves the content at an intermediate frame.
        private static readonly Animation PowerOn = new()
        {
            Duration = TimeSpan.FromMilliseconds(700),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0), new Setter(ScaleTransform.ScaleYProperty, 0.04) } },
                new KeyFrame { Cue = new Cue(0.6), Setters = { new Setter(OpacityProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.02) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
            },
        };

        private CancellationTokenSource? _powerOn;

        private async Task PowerOnAsync()
        {
            _powerOn?.Cancel();
            var cts = _powerOn = new CancellationTokenSource();
            try { await PowerOn.RunAsync(Content, cts.Token); }
            catch (OperationCanceledException) { }
        }
    }
}
