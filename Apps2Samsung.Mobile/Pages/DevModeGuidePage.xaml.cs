using Apps2Samsung.Design;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The Developer Mode guide sheet: renders <see cref="DevModeGuide"/> for the selected TV and re-reads
/// the TV every few seconds while it is open, so it turns green by itself once the TV is fixed.
/// From Install (<c>fromInstall</c>) it ends with Continue the install / Install anyway, and
/// <see cref="Completion"/> says whether to go on; from the TV card it is just a guide.
/// </summary>
public partial class DevModeGuidePage : ContentPage
{
	private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(4);

	private readonly TvSelection _tv;
	private readonly Func<IReadOnlyCollection<string>> _localIps;
	private readonly bool _fromInstall;
	private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private DevModeProblem _problem;
	private IDispatcherTimer? _timer;
	private bool _checking;

	public DevModeGuidePage(TvSelection tv, Func<IReadOnlyCollection<string>> localIps, bool fromInstall)
	{
		InitializeComponent();
		_tv = tv;
		_localIps = localIps;
		_fromInstall = fromInstall;
		AnywayBtn.IsVisible = fromInstall;
		Render();
	}

	/// <summary>True when the user chose to go on with the install (the TV is ready, or Install
	/// anyway); false when the sheet was closed.</summary>
	public Task<bool> Completion => _completion.Task;

	/// <summary>Whether <paramref name="tv"/>'s selected TV needs the guide at all.</summary>
	public static bool IsNeeded(TvSelection tv, IReadOnlyCollection<string> localIps) =>
		tv.HasTv && DevModeGuide.Diagnose(tv.Device, localIps) != DevModeProblem.None;

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_timer = Dispatcher.CreateTimer();
		_timer.Interval = CheckInterval;
		_timer.Tick += async (_, _) => await CheckAsync(showProgress: false);
		if (_problem != DevModeProblem.None)
			_timer.Start();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_timer?.Stop();
		_completion.TrySetResult(false);
	}

	private async Task CheckAsync(bool showProgress)
	{
		if (_checking)
			return;
		_checking = true;
		if (showProgress)
		{
			PrimaryBtn.IsEnabled = false;
			PrimaryBtn.Text = L10n.Get("devGuideChecking");
		}
		try
		{
			await _tv.RefreshSelectedAsync();
		}
		finally
		{
			_checking = false;
			PrimaryBtn.IsEnabled = true;
			Render();
		}
	}

	private void Render()
	{
		// The address the TV can reach us on, which is what its Host PC IP field needs: for a TV
		// on the phone's hotspot that is the hotspot address, not the cellular one (#920).
		var localIp = NetworkInfo.LocalIpFor(_tv.Ip);
		_problem = DevModeGuide.Diagnose(_tv.Device, _localIps());
		var ready = _problem == DevModeProblem.None;

		TitleLabel.Text = L10n.Get(DevModeGuide.TitleKey(_problem));
		SubLabel.Text = string.IsNullOrEmpty(_tv.Ip) ? _tv.DisplayName : $"{_tv.DisplayName} · {_tv.Ip}";
		StatusLabel.Text = DevModeGuide.Status(_problem, _tv.Device, localIp, L10n.Get);
		StatusPill.BackgroundColor = DesignTheme.ColorOf(ready ? "OkSoft" : "WarnSoft");
		StatusDot.Fill = new SolidColorBrush(DesignTheme.ColorOf(ready ? "Ok" : "Warn"));

		PrimaryBtn.Text = L10n.Get(ready ? (_fromInstall ? "devGuideContinueInstall" : "btn_Close") : "devGuideCheckAgain");
		AnywayBtn.IsVisible = _fromInstall && !ready;
		FootLabel.IsVisible = !ready;
		if (ready)
			_timer?.Stop();

		Steps.Clear();
		foreach (var step in DevModeGuide.Steps(_problem))
		{
			if (step.Number > 1)
				Steps.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(56, 0, 0, 0), Color = DesignTheme.ColorOf("Hair") });
			Steps.Add(BuildStep(step, localIp, ready));
		}
	}

	private View BuildStep(DevModeGuideStep step, string? localIp, bool ready)
	{
		var on = step.Needed;
		var badge = new Border
		{
			WidthRequest = 28, HeightRequest = 28, StrokeThickness = 0, VerticalOptions = LayoutOptions.Start,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
			BackgroundColor = DesignTheme.ColorOf(on ? "Accent" : "Fill"),
			Content = new Label
			{
				Text = step.Number.ToString(), FontSize = 13, FontAttributes = FontAttributes.Bold,
				TextColor = DesignTheme.ColorOf(on ? "OnAccent" : "Muted"),
				HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
			},
		};

		var text = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(0, 3, 0, 0) };
		text.Add(new Label { Text = L10n.Get(step.TitleKey), FontSize = 15, FontAttributes = FontAttributes.Bold });
		text.Add(new Label { Text = L10n.Get(step.HintKey), FontSize = 13, TextColor = DesignTheme.ColorOf("Muted") });
		if (step.ShowsLocalIp)
			text.Add(BuildIpRow(localIp));

		var row = new Grid
		{
			ColumnDefinitions = { new ColumnDefinition(28), new ColumnDefinition(GridLength.Star) },
			ColumnSpacing = 12, Padding = new Thickness(16, 12),
			// Steps this TV no longer needs stay readable but step back; once ready none stand out.
			Opacity = on || ready ? 1 : 0.45,
		};
		row.Add(badge, 0, 0);
		row.Add(text, 1, 0);
		return row;
	}

	private View BuildIpRow(string? localIp)
	{
		var row = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
		if (string.IsNullOrEmpty(localIp))
		{
			row.Add(new Label { Text = L10n.Get("devGuideNoLocalIp"), FontSize = 15, TextColor = DesignTheme.ColorOf("Bad") });
			return row;
		}

		row.Add(new Label { Text = localIp, FontSize = 22, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center });
		var copy = new Button
		{
			Text = L10n.Get("lblCopy"), FontSize = 13, FontAttributes = FontAttributes.Bold, HeightRequest = 30,
			MinimumHeightRequest = 30, Padding = new Thickness(12, 0), CornerRadius = 15, VerticalOptions = LayoutOptions.Center,
			BackgroundColor = DesignTheme.ColorOf("AccentSoft"), TextColor = DesignTheme.ColorOf("Accent"),
		};
		copy.Clicked += async (_, _) =>
		{
			await Clipboard.Default.SetTextAsync(localIp);
			copy.Text = L10n.Get("lblCopied");
		};
		row.Add(copy);
		return row;
	}

	private async void OnPrimaryClicked(object? sender, EventArgs e)
	{
		if (_problem != DevModeProblem.None)
		{
			await CheckAsync(showProgress: true);
			return;
		}
		_completion.TrySetResult(_fromInstall);
		await Navigation.PopModalAsync();
	}

	private async void OnAnywayClicked(object? sender, EventArgs e)
	{
		_completion.TrySetResult(true);
		await Navigation.PopModalAsync();
	}

	private async void OnCloseClicked(object? sender, EventArgs e)
	{
		_completion.TrySetResult(false);
		await Navigation.PopModalAsync();
	}
}
