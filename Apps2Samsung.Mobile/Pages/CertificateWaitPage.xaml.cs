using Apps2Samsung.Certificate;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// Holds an install whose signing certificate isn't valid yet, counting down to the moment it
/// becomes valid. "Install now" only unlocks once the countdown reaches zero — before that a Samsung
/// TV would reject the package with "Certificate in signature is not valid yet". Awaiting
/// <see cref="Completion"/> yields true when the user waited it out and chose to continue, false if
/// they cancelled (or dismissed the page).
/// </summary>
public partial class CertificateWaitPage : ContentPage
{
	private readonly TaskCompletionSource<bool> _completion = new();
	private readonly DateTime _validFromUtc;
	private IDispatcherTimer? _timer;

	public CertificateWaitPage(CertificateValidityResult validity)
	{
		InitializeComponent();

		_validFromUtc = validity.NotBeforeUtc ?? DateTime.UtcNow;
		MessageLabel.Text = CertificateValidity.DescribeNotYetValid(validity);
	}

	/// <summary>True once the wait is over and the user chose to continue; false if cancelled.</summary>
	public Task<bool> Completion => _completion.Task;

	protected override void OnAppearing()
	{
		base.OnAppearing();

		Tick();
		_timer = Dispatcher.CreateTimer();
		_timer.Interval = TimeSpan.FromSeconds(1);
		_timer.Tick += (_, _) => Tick();
		_timer.Start();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_timer?.Stop();
		// Dismissed with the system back gesture rather than a button — treat that as cancel so the
		// caller is never left awaiting a result that can't arrive.
		_completion.TrySetResult(false);
	}

	private void Tick()
	{
		var remaining = _validFromUtc - DateTime.UtcNow;
		if (remaining <= TimeSpan.Zero)
		{
			_timer?.Stop();
			CountdownLabel.Text = CertificateValidity.FormatCountdown(TimeSpan.Zero);
			CaptionLabel.Text = "Your certificate is valid now — you can install.";
			InstallNowBtn.IsEnabled = true;
			return;
		}

		CountdownLabel.Text = CertificateValidity.FormatCountdown(remaining);
	}

	private async void OnInstallNowClicked(object? sender, EventArgs e)
	{
		_completion.TrySetResult(true);
		await Navigation.PopModalAsync();
	}

	private async void OnCancelClicked(object? sender, EventArgs e)
	{
		_completion.TrySetResult(false);
		await Navigation.PopModalAsync();
	}
}
