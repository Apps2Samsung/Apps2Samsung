using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The install progress sheet. The Install tab pushes it modally once the pre-install guards have
/// passed, then feeds it: <see cref="SetStep"/> as the install moves on, <see cref="Report"/> with
/// every status line, <see cref="Fail"/> when it goes wrong. Swiping it away (Back) leaves the
/// install running; the TV card keeps showing its status. Cancel raises <see cref="CancelRequested"/>
/// and is offered only until the TV starts installing: from there on the TV finishes on its own and
/// stopping halfway could leave the old copy removed without the new one in place. Main thread only.
/// </summary>
public partial class InstallProgressPage : ContentPage
{
	private readonly List<InstallStep> _order = new();
	private readonly List<string> _log = new();
	private InstallStep _current = InstallStep.Download;
	private bool _failed;

	/// <summary>The user tapped Cancel. The install stops at its next safe point.</summary>
	public event EventHandler? CancelRequested;

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

		foreach (var (step, key) in steps)
		{
			_order.Add(step);
			Steps.Add(L10n.Get(key));
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
		if (step >= InstallStep.Install)
			CancelBtn.IsVisible = false;
		Render(percent);
	}

	/// <summary>A status line: the current step's detail, and a line in the log.</summary>
	public void Report(string message)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;
		_log.Add(message);
		LogLabel.Text = string.Join(Environment.NewLine, _log);
		if (!_failed)
			Steps.SetDetail(_order.IndexOf(_current), message);
	}

	/// <summary>The install failed: the current step turns red with the reason, and Close appears.</summary>
	public void Fail(string message)
	{
		_failed = true;
		_log.Add(message);
		LogLabel.Text = string.Join(Environment.NewLine, _log);
		Steps.Fail(message);
		Bar.ProgressColor = DesignTheme.ColorOf("Bad");
		CancelBtn.IsVisible = false;
		CloseBtn.IsVisible = true;
	}

	/// <summary>Closes the sheet if it is still the page on top (the user may have swiped it away).</summary>
	public async Task CloseAsync()
	{
		if (ReferenceEquals(Navigation.ModalStack.LastOrDefault(), this))
			await Navigation.PopModalAsync();
	}

	private void Render(int? percent)
	{
		var index = _order.IndexOf(_current);
		Steps.SetCurrent(Math.Max(0, index));

		var count = Math.Max(1, _order.Count);
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

	private void OnCancelClicked(object? sender, EventArgs e)
	{
		CancelBtn.IsEnabled = false;
		CancelBtn.Text = L10n.Get("lblCancelling");
		CancelRequested?.Invoke(this, EventArgs.Empty);
	}

	private async void OnCloseClicked(object? sender, EventArgs e) => await CloseAsync();
}
