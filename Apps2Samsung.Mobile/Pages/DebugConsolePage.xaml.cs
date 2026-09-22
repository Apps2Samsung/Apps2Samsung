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

    // A packaged service runs in its own process, so its console is its own debug session next to the
    // app's: a second tunnel, a second protocol connection, one shared transcript.
    private TizenDebugSession? _serviceSession;
    private DevToolsConsole? _serviceConsole;
    private string _serviceId = string.Empty;
    private bool _serviceBusy;
    private bool _networkEnabled;

    // What a network line is tagged with, the way a service's lines are tagged with its id.
    private static readonly string NetworkSource = L10n.Get("lblDebugNetwork");

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
            console.NetworkRequestCompleted += OnNetworkRequestCompleted;
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
            _console.NetworkRequestCompleted -= OnNetworkRequestCompleted;
            try { await _console.DisposeAsync(); } catch (Exception ex) { Trace.WriteLine($"[debug] console teardown: {ex.Message}"); }
            _console = null;
        }

        if (_session is not null)
        {
            try { await _session.DisposeAsync(); } catch (Exception ex) { Trace.WriteLine($"[debug] tunnel teardown: {ex.Message}"); }
            _session = null;
        }

        await CloseServiceAsync();

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

    /// <summary>
    /// Turns the inspector's request timings on or off. Off to begin with, and deliberately: a page
    /// loading a grid of posters reports several events per image, all of them over the SDB tunnel.
    /// </summary>
    private async void OnNetworkClicked(object? sender, EventArgs e)
    {
        var console = _console;
        if (console is null)
            return;

        var enabled = !_networkEnabled;
        try
        {
            await console.SetNetworkEnabledAsync(enabled);
            _networkEnabled = enabled;
            NetworkBtn.Text = L10n.Get(enabled ? "lblDebugNetworkOn" : "lblDebugNetwork");
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug,
                L10n.Get(enabled ? "statusNetworkOn" : "statusNetworkOff"), null, NetworkSource));
        }
        catch (Exception ex)
        {
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                string.Format(L10n.Get("statusNetworkFailed"), ex.Message), null, NetworkSource));
        }
    }

    // Raised off the UI thread when a request finishes. A failure or an error status is a red line:
    // those are the ones being looked for when someone turns this on.
    private void OnNetworkRequestCompleted(NetworkRequest request) =>
        MainThread.BeginInvokeOnMainThread(() => Append(new ConsoleEntry(
            request.Started,
            request.Failed ? ConsoleLevel.Error : ConsoleLevel.Log,
            request.Summary(),
            null,
            NetworkSource)));

    /// <summary>
    /// Attaches a second console to a packaged service of the app, or drops the one that is attached.
    /// The service is relaunched in debug mode the same way the app was, because that is the only
    /// launch the TV hands an inspector port to — so this restarts the service, and with it whatever
    /// the app was getting from it.
    /// </summary>
    private async void OnServiceLogClicked(object? sender, EventArgs e)
    {
        if (_serviceBusy)
            return;

        if (_serviceConsole is not null)
        {
            var attached = _serviceId;
            _serviceBusy = true;
            try
            {
                await CloseServiceAsync();
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug,
                    string.Format(L10n.Get("statusServiceLogDetached"), attached), null, attached));
            }
            finally
            {
                _serviceBusy = false;
                UpdateServiceButton();
            }
            return;
        }

        var suggestion = await SuggestServiceIdAsync();
        var id = await DisplayPromptAsync(
            L10n.Get("lblServiceLog"),
            L10n.Get("hintServiceLog"),
            L10n.Get("lblOk"),
            L10n.Get("lblCancel"),
            placeholder: L10n.Get("lblServiceIdHint"),
            initialValue: suggestion);

        id = id?.Trim() ?? string.Empty;
        if (id.Length == 0)
            return;

        _serviceBusy = true;
        SetBusy(true);
        try
        {
            // Same reason as the app: debug mode only reports a port for the launch it performs.
            try
            {
                await _sdb.ShellAsync(_tvIp, $"0 was_kill {id}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[debug] pre-stop of service {id} failed (continuing): {ex.Message}");
            }

            _serviceSession = await TizenAppDebugger.StartAsync(_sdb, _tvIp, id);

            var targets = await DevToolsInspector.ListTargetsAsync(_serviceSession.LocalPort);
            var console = new DevToolsConsole();
            console.EntryReceived += OnServiceEntryReceived;
            console.Disconnected += OnServiceDisconnected;
            await console.ConnectAsync(targets[0].WebSocketUrl);

            _serviceConsole = console;
            _serviceId = id;
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Info,
                string.Format(L10n.Get("statusServiceLogAttached"), id), null, id));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[debug] attach to service {id} failed: {ex}");
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                string.Format(L10n.Get("statusServiceLogAttachFailed"), id, ex.Message), null, id));
            await CloseServiceAsync();
        }
        finally
        {
            _serviceBusy = false;
            SetBusy(false);
            UpdateServiceButton();
        }
    }

    // The TV lists what is installed; a packaged service, when it lists one, is an id under the app's
    // own package. Only ever a suggestion — the prompt takes a typed id just as well.
    private async Task<string> SuggestServiceIdAsync()
    {
        if (_serviceId.Length > 0)
            return _serviceId;

        try
        {
            var listed = await _sdb.AppsAsync(_tvIp);
            var siblings = TizenPackageServices.SiblingIdsOf(TizenInstalledApps.Parse(listed?.Output), _tizenId);
            return siblings.Count > 0 ? siblings[0] : string.Empty;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[debug] service suggestions for {_tizenId}: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task CloseServiceAsync()
    {
        if (_serviceConsole is not null)
        {
            _serviceConsole.EntryReceived -= OnServiceEntryReceived;
            _serviceConsole.Disconnected -= OnServiceDisconnected;
            try { await _serviceConsole.DisposeAsync(); }
            catch (Exception ex) { Trace.WriteLine($"[debug] service console teardown: {ex.Message}"); }
            _serviceConsole = null;
        }

        if (_serviceSession is not null)
        {
            try { await _serviceSession.DisposeAsync(); }
            catch (Exception ex) { Trace.WriteLine($"[debug] service tunnel teardown: {ex.Message}"); }
            _serviceSession = null;
        }
    }

    // Raised off the UI thread by the service's receive loop. Tagged with the service id so the two
    // processes stay apart in the one transcript.
    private void OnServiceEntryReceived(ConsoleEntry entry) =>
        MainThread.BeginInvokeOnMainThread(() => Append(entry with { Source = _serviceId }));

    private void OnServiceDisconnected(string? reason) => MainThread.BeginInvokeOnMainThread(() =>
    {
        // A service that exits is exactly what this console is here to show, so the reason is a line
        // in the log rather than an alert.
        Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
            string.Format(L10n.Get("statusServiceLogEnded"), _serviceId,
                reason ?? L10n.Get("statusServiceLogEndedQuietly")), null, _serviceId));
        UpdateServiceButton();
    });

    private void UpdateServiceButton() =>
        ServiceBtn.Text = _serviceConsole is not null
            ? L10n.Get("lblServiceDetach")
            : L10n.Get("lblServiceLog");

    /// <summary>
    /// GETs a path from a port on the TV's loopback address and writes the answer into the transcript —
    /// the diagnostics endpoint a packaged service publishes for itself, which otherwise takes a shell
    /// on the TV that a retail set does not hand out.
    /// </summary>
    private async void OnEndpointClicked(object? sender, EventArgs e)
    {
        var typedPort = await DisplayPromptAsync(
            L10n.Get("lblServiceEndpoint"),
            L10n.Get("hintServiceEndpoint"),
            L10n.Get("lblOk"),
            L10n.Get("lblCancel"),
            placeholder: L10n.Get("lblServiceEndpointPort"),
            keyboard: Keyboard.Numeric);

        if (string.IsNullOrWhiteSpace(typedPort))
            return;

        if (!int.TryParse(typedPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            await DisplayAlert(L10n.Get("lblServiceEndpoint"), L10n.Get("statusServicePortInvalid"), L10n.Get("lblOk"));
            return;
        }

        var typedPath = await DisplayPromptAsync(
            L10n.Get("lblServiceEndpoint"),
            L10n.Get("lblServiceEndpointPathAsk"),
            L10n.Get("lblOk"),
            L10n.Get("lblCancel"),
            placeholder: L10n.Get("lblServiceEndpointPath"),
            initialValue: L10n.Get("lblServiceEndpointPath"));

        if (typedPath is null)
            return;

        var path = TizenServiceEndpoint.NormalizePath(typedPath);
        var source = $"127.0.0.1:{port}";

        SetBusy(true);
        try
        {
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug,
                $"> GET http://{source}{path}", null, source));

            var result = await TizenServiceEndpoint.QueryAsync(_sdb, _tvIp, port, path);

            Append(new ConsoleEntry(DateTimeOffset.Now,
                result.IsSuccess ? ConsoleLevel.Info : ConsoleLevel.Warning,
                string.Format(L10n.Get("statusServiceEndpointAnswered"),
                    result.StatusCode, result.ReasonPhrase ?? string.Empty,
                    (int)result.Duration.TotalMilliseconds), null, source));

            if (!string.IsNullOrWhiteSpace(result.Body))
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Log, result.Body, null, source));

            if (result.Masked)
            {
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug,
                    L10n.Get("statusServiceEndpointMasked"), null, source));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[debug] endpoint query {source}{path} failed: {ex}");
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                string.Format(L10n.Get("statusServiceEndpointFailed"), $"{source}{path}", ex.Message),
                null, source));
        }
        finally
        {
            SetBusy(false);
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

        /// <summary>Which process this line came from; empty for the app's own console.</summary>
        public string? Source => _entry.Source;
        public bool HasSource => !string.IsNullOrEmpty(_entry.Source);

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
            var source = HasSource ? $"[{_entry.Source}] " : string.Empty;
            var origin = HasOrigin ? $"   ({_entry.Origin})" : string.Empty;
            return $"{Clock} {level,-7} {source}{_entry.Text}{origin}";
        }
    }
}
