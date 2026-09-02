using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Diagnostics;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Mobile.Pages;

/// <summary>
/// The console for an app running on the TV — this head's answer to the desktop's
/// <c>chrome://inspect</c> handoff, which has no equivalent in Chrome on Android.
///
/// Owns the whole debug lifecycle: stop the app, relaunch it in debug mode, tunnel the inspector back
/// here (shared Core <see cref="TizenAppDebugger"/>), attach over the DevTools protocol
/// (<see cref="DevToolsConsole"/>), and tear all of it down again when the page goes away.
/// </summary>
public partial class DebugConsolePage : ContentPage
{
    // A phone has far less room for scrollback than a desktop console, and every retained row is a
    // live view. Old lines drop off the top once this many are held.
    private const int MaxRows = 2000;

    private readonly ISdbEngine _sdb;
    private readonly string _tvIp;
    private readonly string _tizenId;
    private readonly string _appName;
    private readonly ObservableCollection<ConsoleRow> _rows = new();

    private TizenDebugSession? _session;
    private DevToolsConsole? _console;
    private bool _detaching;
    private bool _scrollQueued;

    public DebugConsolePage(ISdbEngine sdb, string tvIp, string tizenId, string appName)
    {
        InitializeComponent();
        _sdb = sdb;
        _tvIp = tvIp;
        _tizenId = tizenId;
        _appName = appName;

        LogList.ItemsSource = _rows;
        SubtitleLabel.Text = $"{_appName} · {_tizenId}";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Re-appearing after the console was torn down (back out of a pushed page) must not attach a
        // second time — and re-attaching would restart the app under the user.
        if (_session is not null || _detaching)
            return;

        await AttachAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await DetachAsync();
    }

    private async Task AttachAsync()
    {
        SetStatus(ConsoleStatus.Attaching);
        SetBusy(true);
        try
        {
            // Debug mode only reports an inspector port for the launch it performs itself, so the app
            // has to be down first. It may well not be running — that is not a failure.
            try
            {
                await _sdb.ShellAsync(_tvIp, $"0 was_kill {_tizenId}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[debug] pre-stop of {_tizenId} failed (continuing): {ex.Message}");
            }

            _session = await TizenAppDebugger.StartAsync(_sdb, _tvIp, _tizenId);

            var targets = await DevToolsInspector.ListTargetsAsync(_session.LocalPort);
            var target = await PickTargetAsync(targets);
            if (target is null)
            {
                await DetachAsync();
                await Navigation.PopAsync();
                return;
            }

            var console = new DevToolsConsole();
            console.EntryReceived += OnEntryReceived;
            console.Disconnected += OnDisconnected;
            await console.ConnectAsync(target.WebSocketUrl);
            _console = console;

            SetStatus(ConsoleStatus.Attached);
            SubtitleLabel.Text = string.IsNullOrWhiteSpace(target.Title)
                ? $"{_appName} · {_tizenId}"
                : $"{_appName} · {target.Title}";
            EvalEntry.IsEnabled = true;
            EvalBtn.IsEnabled = true;

            // The log is only live while this page is up, so let the screen stay on rather than have
            // the session die under a screen timeout while the user watches the TV.
            DeviceDisplay.Current.KeepScreenOn = true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[debug] attach to {_tizenId} failed: {ex}");
            SetStatus(ConsoleStatus.Detached);
            await DetachAsync();
            await DisplayAlert(
                L10n.Get("lblDebugConsole"),
                string.Format(L10n.Get("statusDebugAttachFailed"), _appName, ex.Message),
                L10n.Get("lblOk"));
            await Navigation.PopAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    // A .wgt normally has exactly one page; ask only when the TV really does report several, so the
    // common case stays a single tap.
    private async Task<DevToolsTarget?> PickTargetAsync(IReadOnlyList<DevToolsTarget> targets)
    {
        if (targets.Count == 1)
            return targets[0];

        var labels = targets.Select(Describe).ToArray();
        var chosen = await DisplayActionSheet(
            L10n.Get("lblDebugPickTarget"), L10n.Get("lblCancel"), null, labels);

        var index = Array.IndexOf(labels, chosen);
        return index < 0 ? null : targets[index];
    }

    private static string Describe(DevToolsTarget target) =>
        string.IsNullOrWhiteSpace(target.Title) ? target.Url : target.Title;

    private async Task DetachAsync()
    {
        if (_detaching)
            return;
        _detaching = true;

        DeviceDisplay.Current.KeepScreenOn = false;

        if (_console is not null)
        {
            _console.EntryReceived -= OnEntryReceived;
            _console.Disconnected -= OnDisconnected;
            try { await _console.DisposeAsync(); } catch (Exception ex) { Trace.WriteLine($"[debug] console teardown: {ex.Message}"); }
            _console = null;
        }

        if (_session is not null)
        {
            try { await _session.DisposeAsync(); } catch (Exception ex) { Trace.WriteLine($"[debug] tunnel teardown: {ex.Message}"); }
            _session = null;
        }

        _detaching = false;
    }

    // Raised off the UI thread by the receive loop.
    private void OnEntryReceived(ConsoleEntry entry) =>
        MainThread.BeginInvokeOnMainThread(() => Append(entry));

    private void OnDisconnected(string? reason) => MainThread.BeginInvokeOnMainThread(() =>
    {
        SetStatus(ConsoleStatus.Detached);
        EvalEntry.IsEnabled = false;
        EvalBtn.IsEnabled = false;

        if (reason is not null)
        {
            Append(new ConsoleEntry(
                DateTimeOffset.Now,
                ConsoleLevel.Error,
                string.Format(L10n.Get("statusDebugConnectionLost"), reason),
                null));
        }
    });

    private void Append(ConsoleEntry entry)
    {
        _rows.Add(new ConsoleRow(entry));
        while (_rows.Count > MaxRows)
            _rows.RemoveAt(0);

        QueueScrollToEnd();
    }

    // An app can log in bursts, and scrolling once per line makes the list stutter while it is
    // still measuring the previous one. One scroll per dispatcher turn keeps up with the tail
    // without paying for every intermediate line.
    private void QueueScrollToEnd()
    {
        if (_scrollQueued)
            return;

        _scrollQueued = true;
        Dispatcher.Dispatch(() =>
        {
            _scrollQueued = false;
            if (_rows.Count > 0)
                LogList.ScrollTo(_rows.Count - 1, position: ScrollToPosition.End, animate: false);
        });
    }

    private async void OnEvaluateClicked(object? sender, EventArgs e)
    {
        var expression = EvalEntry.Text;
        if (string.IsNullOrWhiteSpace(expression) || _console is null)
            return;

        EvalEntry.Text = string.Empty;

        // Echo the expression so the transcript reads like a session rather than bare answers.
        Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug, $"> {expression}", null));
        try
        {
            var result = await _console.EvaluateAsync(expression);
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Info, result, null));
        }
        catch (Exception ex)
        {
            Append(new ConsoleEntry(
                DateTimeOffset.Now,
                ConsoleLevel.Error,
                string.Format(L10n.Get("statusDebugEvaluateFailed"), ex.Message),
                null));
        }
    }

    // Matches Settings → Diagnostics → "Share debug log": Android has no save dialog, so the
    // transcript goes to a cache file and out through the share sheet.
    private async void OnShareClicked(object? sender, EventArgs e)
    {
        if (_rows.Count == 0)
            return;

        try
        {
            var transcript = new StringBuilder();
            transcript.AppendLine($"{_appName} ({_tizenId}) on {_tvIp}");
            transcript.AppendLine();
            foreach (var row in _rows)
                transcript.AppendLine(row.AsTextLine());

            var path = Path.Combine(
                FileSystem.CacheDirectory,
                $"tv-console-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(path, transcript.ToString());

            await Share.Default.RequestAsync(new ShareFileRequest(
                L10n.Get("lblDebugConsole"), new ShareFile(path)));
        }
        catch (Exception ex)
        {
            await DisplayAlert(L10n.Get("lblDebugConsole"),
                string.Format(L10n.Get("statusLogShareFailed"), ex.Message), L10n.Get("btn_Close"));
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopAsync();

    private enum ConsoleStatus { Attaching, Attached, Detached }

    private void SetStatus(ConsoleStatus status)
    {
        // Same colour language as the desktop's TV-log window: blue while working, green attached,
        // grey once it is over.
        (StatusLabel.Text, StatusPill.BackgroundColor) = status switch
        {
            ConsoleStatus.Attaching => (string.Format(L10n.Get("statusDebugAttaching"), _appName), Color.FromArgb("#2980B9")),
            ConsoleStatus.Attached => (L10n.Get("statusDebugAttached"), Color.FromArgb("#27AE60")),
            _ => (L10n.Get("statusDebugDetached"), Color.FromArgb("#7F8C8D")),
        };
    }

    private void SetBusy(bool busy)
    {
        Busy.IsVisible = busy;
        Busy.IsRunning = busy;
    }

    /// <summary>One console line, shaped for the row template.</summary>
    private sealed class ConsoleRow
    {
        private readonly ConsoleEntry _entry;

        public ConsoleRow(ConsoleEntry entry) => _entry = entry;

        public string Clock => _entry.Timestamp.ToString("HH:mm:ss");
        public string Text => _entry.Text;
        public string? Origin => _entry.Origin;
        public bool HasOrigin => !string.IsNullOrEmpty(_entry.Origin);

        public Color Color => _entry.Level switch
        {
            ConsoleLevel.Error => Color.FromArgb("#FF6B6B"),
            ConsoleLevel.Warning => Color.FromArgb("#E6B860"),
            ConsoleLevel.Info => Color.FromArgb("#6FC3DF"),
            ConsoleLevel.Debug => Color.FromArgb("#8E99A4"),
            _ => Color.FromArgb("#DCE3EA"),
        };

        public string AsTextLine()
        {
            var level = _entry.Level.ToString().ToUpperInvariant();
            var origin = HasOrigin ? $"   ({_entry.Origin})" : string.Empty;
            return $"{Clock} {level,-7} {_entry.Text}{origin}";
        }
    }
}
