using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// Holds an install whose signing certificate isn't valid yet and closes with true once it is
    /// (a Samsung TV refuses it before that), or with false on Cancel or when closed.
    /// </summary>
    public partial class CertificateWaitWindow : Window
    {
        private readonly DateTime _validFromUtc;
        private readonly Func<string, string> _localize = key => key;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

        public string TitleText { get; } = string.Empty;
        public string ValidInText { get; } = string.Empty;
        public string ContinuesText { get; } = string.Empty;
        public string BodyText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;

        public CertificateWaitWindow()
        {
            InitializeComponent();
        }

        public CertificateWaitWindow(DateTime validFromLocal, Func<string, string> localize) : this()
        {
            _localize = localize;
            _validFromUtc = validFromLocal.ToUniversalTime();
            TitleText = localize("certificateNotYetValidTitle");
            ValidInText = localize("certValidIn");
            ContinuesText = localize("certContinuesByItself");
            BodyText = localize("certNotYetValidBody");
            CancelText = localize("lblCancel");
            DataContext = this;

            _timer.Tick += (_, _) => Tick();
            Opened += (_, _) =>
            {
                Tick();
                _timer.Start();
            };
            Closed += (_, _) => _timer.Stop();
        }

        private void Tick()
        {
            var remaining = _validFromUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                CountdownText.Text = CertificateValidity.FormatCountdown(remaining);
                return;
            }

            _timer.Stop();
            CountdownText.Text = CertificateValidity.FormatCountdown(TimeSpan.Zero);
            CaptionText.Text = _localize("certificateCountdownReady");
            Close(true);
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
    }
}
