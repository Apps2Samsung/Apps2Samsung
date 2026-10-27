using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The sheet for a community package's <c>user_settings</c> (#791): one entry per field, the
/// catalog's help page as a link, inline validation from <see cref="UserSettingsStore"/>.
/// <see cref="Result"/> completes with the values by key on Install, null on Cancel or swipe-away.
/// </summary>
public partial class UserSettingsSheetPage : ContentPage
{
	private sealed record Row(UserSettingsField Field, Entry Entry, Label Error);

	private readonly List<Row> _rows = new();
	private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _result = new();
	private readonly string? _helpUrl;
	private bool _closing;

	public Task<IReadOnlyDictionary<string, string>?> Result => _result.Task;

	public UserSettingsSheetPage(string appName, UserSettingsSpec spec, IReadOnlyDictionary<string, string> current)
	{
		InitializeComponent();
		_helpUrl = spec.HelpUrl;
		TitleLabel.Text = string.Format(L10n.Get("lblAppNeedsDetails"), appName);
		if (!string.IsNullOrWhiteSpace(_helpUrl))
		{
			HelpLink.Text = L10n.Get("lblWhereToGetThese") + " ↗";
			HelpLink.IsVisible = true;
		}

		foreach (var field in spec.Fields)
		{
			var label = new Label
			{
				Text = field.Required ? field.Label : $"{field.Label} ({L10n.Get("lblOptional")})",
				FontAttributes = FontAttributes.Bold, FontSize = 14,
			};
			var entry = new Entry
			{
				Text = current.TryGetValue(field.Key, out var value) ? value : string.Empty,
				Keyboard = field.IsNumber ? Keyboard.Numeric : Keyboard.Plain,
				// What an empty optional field installs with.
				Placeholder = field.Default,
				BackgroundColor = Colors.Transparent,
			};
			var box = new Border { Style = (Style)Resources["Field"], Content = entry };
			var error = new Label { FontSize = 12, TextColor = DesignTheme.ColorOf("Bad"), IsVisible = false };
			var row = new Row(field, entry, error);
			// The error goes as soon as the value is right again.
			entry.TextChanged += (_, _) => { if (error.IsVisible) Validate(row); };
			_rows.Add(row);
			Fields.Add(new VerticalStackLayout { Spacing = 5, Children = { label, box, error } });
		}
	}

	private bool Validate(Row row)
	{
		var key = UserSettingsStore.Validate(row.Field, row.Entry.Text);
		row.Error.Text = key is null ? string.Empty : UserSettingsStore.Message(row.Field, key, L10n.Get);
		row.Error.IsVisible = key is not null;
		return key is null;
	}

	private async void OnInstallClicked(object? sender, EventArgs e)
	{
		Row? firstBad = null;
		foreach (var row in _rows)
		{
			if (!Validate(row))
				firstBad ??= row;
		}
		if (firstBad is not null)
		{
			firstBad.Entry.Focus();
			return;
		}

		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var row in _rows)
			values[row.Field.Key] = (row.Entry.Text ?? string.Empty).Trim();
		_closing = true;
		_result.TrySetResult(values);
		await Navigation.PopModalAsync();
	}

	private async void OnCancelClicked(object? sender, EventArgs e)
	{
		_closing = true;
		_result.TrySetResult(null);
		await Navigation.PopModalAsync();
	}

	private async void OnHelpTapped(object? sender, TappedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(_helpUrl))
			return;
		try { await Launcher.Default.OpenAsync(_helpUrl); }
		catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[UserSettings] Couldn't open the help page: {ex.Message}"); }
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// Swiped away or Back: the same as Cancel.
		if (!_closing)
			_result.TrySetResult(null);
	}
}
