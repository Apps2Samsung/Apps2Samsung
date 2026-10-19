using System.Globalization;
using Apps2Samsung.Diagnostics;
using Apps2Samsung.Mobile.Controls;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Mobile.Resources.Styles;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>One log entry as the log viewer's list shows it.</summary>
public sealed record LogViewerRow(string Time, string Message, Color Ink, Color Back);

/// <summary>
/// The debug log viewer: one session's log (<see cref="LogReader"/>), with an All / Warnings /
/// Errors filter, a search, and Copy / Share. The ⋯ menu switches to an earlier session.
/// </summary>
public partial class LogViewerPage : ContentPage
{
	private const string DocumentIcon = "M6,3 L14,3 L19,8 L19,21 L6,21 Z M14,3 L14,8 L19,8";

	private readonly string _directory;
	private IReadOnlyList<LogSession> _sessions = Array.Empty<LogSession>();
	private LogSession? _session;
	private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();
	private LogEntryLevel _minimum = LogEntryLevel.Info;

	public LogViewerPage()
	{
		InitializeComponent();
		_directory = Path.Combine(FileSystem.AppDataDirectory, "Logs");
		ShowFilter();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_sessions = LogReader.Sessions(_directory);
		// Keep the session that was open; reload it, since the current one keeps growing.
		Load(_sessions.FirstOrDefault(s => s.Path == _session?.Path) ?? _sessions.FirstOrDefault());
	}

	private void Load(LogSession? session)
	{
		_session = session;
		_entries = session is null ? Array.Empty<LogEntry>() : LogReader.Read(session.Path);
		TopBar.Subtitle = session is null ? string.Empty : SessionName(session);
		Render();
	}

	private static string SessionName(LogSession session) => session.IsCurrent
		? L10n.Get("lblLogThisSession")
		: string.Format(L10n.Get("lblLogSessionOf"), session.Started.ToString("g", CultureInfo.CurrentCulture));

	private void Render()
	{
		var shown = LogReader.Filter(_entries, _minimum, SearchEntry.Text).ToList();
		Lines.ItemsSource = shown.Select(ToRow).ToList();
		FootLabel.Text = _session is null
			? L10n.Get("statusNoLogFile")
			: $"{Path.GetFileName(_session.Path)} · {string.Format(L10n.Get("lblLogLineCount"), _entries.Count)}";
		CopyBtn.Text = L10n.Get("lblCopy");
	}

	private static LogViewerRow ToRow(LogEntry entry) => entry.Level switch
	{
		LogEntryLevel.Error => new LogViewerRow(Time(entry), entry.Message, DesignTheme.ColorOf("Bad"), DesignTheme.ColorOf("BadSoft")),
		LogEntryLevel.Warning => new LogViewerRow(Time(entry), entry.Message, DesignTheme.ColorOf("Warn"), DesignTheme.ColorOf("WarnSoft")),
		_ => new LogViewerRow(Time(entry), entry.Message, DesignTheme.ColorOf("Ink"), Colors.Transparent),
	};

	private static string Time(LogEntry entry) => entry.Time?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;

	private void ShowFilter()
	{
		FilterAll.BackgroundColor = Segment(_minimum == LogEntryLevel.Info);
		FilterWarnings.BackgroundColor = Segment(_minimum == LogEntryLevel.Warning);
		FilterErrors.BackgroundColor = Segment(_minimum == LogEntryLevel.Error);
	}

	private static Color Segment(bool on) => on ? DesignTheme.ColorOf("Surface") : Colors.Transparent;

	private void OnFilterClicked(object? sender, EventArgs e)
	{
		_minimum = sender == FilterErrors ? LogEntryLevel.Error : sender == FilterWarnings ? LogEntryLevel.Warning : LogEntryLevel.Info;
		ShowFilter();
		Render();
	}

	private void OnSearchChanged(object? sender, TextChangedEventArgs e) => Render();

	private async void OnMoreClicked(object? sender, EventArgs e)
	{
		if (_sessions.Count == 0)
			return;
		// The ten newest; older ones are still in the folder for Share from the file manager.
		var shown = _sessions.Take(10).ToList();
		var picked = await MoreMenu.ShowAsync(this, shown.Select(s => new MoreMenuItem(SessionName(s), DocumentIcon)).ToArray());
		if (picked >= 0)
			Load(shown[picked]);
	}

	private async void OnCopyClicked(object? sender, EventArgs e)
	{
		await Clipboard.Default.SetTextAsync(LogReader.ToText(LogReader.Filter(_entries, _minimum, SearchEntry.Text)));
		CopyBtn.Text = L10n.Get("lblCopied");
	}

	// The whole file, whatever the filter: whoever reads it wants the full transcript.
	private async void OnShareClicked(object? sender, EventArgs e)
	{
		if (_session is null || !File.Exists(_session.Path))
		{
			await DisplayAlert(L10n.Get("lblDebugLog"), L10n.Get("statusNoLogFile"), L10n.Get("btn_Close"));
			return;
		}

		try
		{
			await Share.Default.RequestAsync(new ShareFileRequest
			{
				Title = L10n.Get("lblDebugLog"),
				File = new ShareFile(_session.Path),
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert(L10n.Get("lblDebugLog"), string.Format(L10n.Get("statusLogShareFailed"), ErrorText.Describe(ex, "log")), L10n.Get("btn_Close"));
		}
	}
}
