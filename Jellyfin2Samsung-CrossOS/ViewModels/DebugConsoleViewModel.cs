using Apps2Samsung.Diagnostics;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// The console for an app running on the TV — the desktop twin of the mobile head's
    /// <c>DebugConsolePage</c>, built on the same Core pieces (<see cref="Apps2Samsung.Sdb.TizenAppDebugger"/>
    /// via the installer service, <see cref="DevToolsInspector"/>, <see cref="DevToolsConsole"/>).
    ///
    /// Before this the desktop only forwarded the inspector port and tried to launch Chrome at
    /// <c>chrome://inspect</c>, leaving the user to find the tab, enable discovery and click through.
    /// Now the log streams straight into this window, the same way it does on the phone; the full
    /// DevTools frontend the TV hosts is one click away for anyone who wants more than the console.
    ///
    /// Owns the whole debug lifecycle: stop the app, relaunch it in debug mode, tunnel the inspector
    /// back here, attach over the DevTools protocol, and tear it all down again when the window closes.
    /// </summary>
    public partial class DebugConsoleViewModel : ViewModelBase
    {
        // Enough scrollback for a long session without every retained row costing a live control.
        private const int MaxRows = 5000;

        private readonly ITizenInstallerService _installer;
        private readonly IDialogService _dialogService;
        private readonly string _tvIp;
        private readonly InstalledApp _app;

        private IAsyncDisposable? _session;
        private int _localPort;
        private DevToolsConsole? _console;
        private bool _switchingTarget;
        private bool _detaching;

        public ObservableCollection<ConsoleRowViewModel> Rows { get; } = new();
        public ObservableCollection<DevToolsTarget> Targets { get; } = new();

        public string AppName => _app.DisplayName;
        public string TizenId => _app.TizenId;
        public string WindowTitle => $"{"lblDebugConsole".Localized()} · {_app.DisplayName}";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEvaluate))]
        [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
        private bool isAttaching;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEvaluate))]
        [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
        private bool isAttached;

        /// <summary>True once the tunnel and console are gone; the window may close for real.</summary>
        public bool IsDetached { get; private set; }

        [ObservableProperty]
        private string statusText = "statusDebugDetached".Localized();

        [ObservableProperty]
        private IBrush statusBrush = DetachedBrush;

        [ObservableProperty]
        private string subtitle = string.Empty;

        [ObservableProperty]
        private string expression = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanOpenInBrowser))]
        private DevToolsTarget? selectedTarget;

        public bool HasMultipleTargets => Targets.Count > 1;
        public bool HasRows => Rows.Count > 0;
        public bool CanEvaluate => IsAttached && !IsAttaching;
        public bool CanOpenInBrowser => SelectedTarget is not null && !IsAttaching;

        /// <summary>Raised once the console is streaming, with the local inspector port.</summary>
        public event Action<int>? Attached;

        /// <summary>Raised when the view model wants its window closed.</summary>
        public event Action? OnRequestClose;

        /// <summary>Raised after <see cref="DetachAsync"/> has torn everything down.</summary>
        public event Action? Detached;

        // Same colour language as the TV-log window and the phone: blue while working, green attached,
        // grey once it is over.
        private static readonly IBrush AttachingBrush = new SolidColorBrush(Color.Parse("#2980B9"));
        private static readonly IBrush AttachedBrush = new SolidColorBrush(Color.Parse("#27AE60"));
        private static readonly IBrush DetachedBrush = new SolidColorBrush(Color.Parse("#7F8C8D"));

        public DebugConsoleViewModel(ITizenInstallerService installer, IDialogService dialogService, string tvIp, InstalledApp app)
        {
            _installer = installer;
            _dialogService = dialogService;
            _tvIp = tvIp;
            _app = app;
            Subtitle = $"{app.DisplayName} · {app.TizenId}";

            Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
            Targets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleTargets));
        }

        /// <summary>Stops the app, relaunches it in debug mode and attaches the console. Call once, from the window.</summary>
        public async Task AttachAsync()
        {
            if (_session is not null || _detaching)
                return;

            SetStatus(ConsoleStatus.Attaching);
            IsAttaching = true;
            try
            {
                // Debug mode only reports an inspector port for the launch it performs itself, so the
                // app has to be down first. It may well not be running — that is not a failure.
                try
                {
                    await _installer.StopAppAsync(_tvIp, _app.TizenId);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[debug] pre-stop of {_app.TizenId} failed (continuing): {ex.Message}");
                }

                var (port, session) = await _installer.DebugAppAsync(_tvIp, _app.TizenId);
                _session = session;
                _localPort = port;

                var targets = await DevToolsInspector.ListTargetsAsync(port);
                foreach (var target in targets)
                    Targets.Add(target);

                // A .wgt normally has exactly one page. When the TV reports several, attach to the
                // first and let the picker in the header switch — no modal question on the way in.
                await ConnectAsync(targets[0]);
                Attached?.Invoke(port);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[debug] attach to {_app.TizenId} failed: {ex}");
                SetStatus(ConsoleStatus.Detached);
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                    string.Format("statusDebugAttachFailed".Localized(), _app.DisplayName, ex.Message), null));
                await DetachAsync();
                await _dialogService.ShowErrorAsync(
                    string.Format("statusDebugAttachFailed".Localized(), _app.DisplayName, ex.Message));
            }
            finally
            {
                IsAttaching = false;
            }
        }

        private async Task ConnectAsync(DevToolsTarget target)
        {
            var console = new DevToolsConsole();
            console.EntryReceived += OnEntryReceived;
            console.Disconnected += OnDisconnected;
            await console.ConnectAsync(target.WebSocketUrl);
            _console = console;

            _switchingTarget = true;
            SelectedTarget = target;
            _switchingTarget = false;

            IsAttached = true;
            SetStatus(ConsoleStatus.Attached);
            Subtitle = string.IsNullOrWhiteSpace(target.Title)
                ? $"{_app.DisplayName} · {_app.TizenId}"
                : $"{_app.DisplayName} · {target.Title}";
        }

        partial void OnSelectedTargetChanged(DevToolsTarget? value)
        {
            if (_switchingTarget || value is null || _session is null || _detaching)
                return;
            _ = SwitchTargetAsync(value);
        }

        // Re-attach the console to another of the app's pages; the tunnel stays up.
        private async Task SwitchTargetAsync(DevToolsTarget target)
        {
            IsAttaching = true;
            try
            {
                await CloseConsoleAsync();
                await ConnectAsync(target);
            }
            catch (Exception ex)
            {
                SetStatus(ConsoleStatus.Detached);
                IsAttached = false;
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                    string.Format("statusDebugAttachFailed".Localized(), _app.DisplayName, ex.Message), null));
            }
            finally
            {
                IsAttaching = false;
            }
        }

        private async Task CloseConsoleAsync()
        {
            if (_console is null)
                return;

            _console.EntryReceived -= OnEntryReceived;
            _console.Disconnected -= OnDisconnected;
            try { await _console.DisposeAsync(); }
            catch (Exception ex) { Trace.WriteLine($"[debug] console teardown: {ex.Message}"); }
            _console = null;
            IsAttached = false;
        }

        /// <summary>Drops the console and the inspector tunnel. Safe to call more than once.</summary>
        public async Task DetachAsync()
        {
            if (_detaching || IsDetached)
                return;
            _detaching = true;

            await CloseConsoleAsync();

            if (_session is not null)
            {
                try { await _session.DisposeAsync(); }
                catch (Exception ex) { Trace.WriteLine($"[debug] tunnel teardown: {ex.Message}"); }
                _session = null;
            }

            SetStatus(ConsoleStatus.Detached);
            IsDetached = true;
            _detaching = false;
            Detached?.Invoke();
        }

        // Raised off the UI thread by the receive loop.
        private void OnEntryReceived(ConsoleEntry entry) =>
            Dispatcher.UIThread.Post(() => Append(entry));

        private void OnDisconnected(string? reason) => Dispatcher.UIThread.Post(() =>
        {
            IsAttached = false;
            SetStatus(ConsoleStatus.Detached);

            if (reason is not null)
            {
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                    string.Format("statusDebugConnectionLost".Localized(), reason), null));
            }
        });

        private void Append(ConsoleEntry entry)
        {
            Rows.Add(new ConsoleRowViewModel(entry));
            while (Rows.Count > MaxRows)
                Rows.RemoveAt(0);
        }

        [RelayCommand]
        private async Task Evaluate()
        {
            var expression = Expression;
            if (string.IsNullOrWhiteSpace(expression) || _console is null)
                return;

            Expression = string.Empty;

            // Echo the expression so the transcript reads like a session rather than bare answers.
            Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Debug, $"> {expression}", null));
            try
            {
                var result = await _console.EvaluateAsync(expression);
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Info, result, null));
            }
            catch (Exception ex)
            {
                Append(new ConsoleEntry(DateTimeOffset.Now, ConsoleLevel.Error,
                    string.Format("statusDebugEvaluateFailed".Localized(), ex.Message), null));
            }
        }

        // The TV serves the DevTools frontend itself, so the system browser can open it directly —
        // no chrome://inspect, no discovery settings, and it works with whichever Chromium-based
        // browser (Chrome, Edge, Brave, Chromium…) is the default, on Windows, macOS and Linux alike.
        [RelayCommand]
        private async Task OpenInBrowser()
        {
            var target = SelectedTarget;
            if (target is null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = target.FrontendUrl.ToString(), UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync("lblDebugConsole".Localized(),
                    string.Format("statusDebugOpenBrowserFailed".Localized(), target.FrontendUrl, ex.Message));
            }
        }

        [RelayCommand]
        private async Task CopyLog(Window? owner)
        {
            if (owner?.Clipboard is null || Rows.Count == 0)
                return;
            await owner.Clipboard.SetTextAsync(Transcript());
        }

        [RelayCommand]
        private async Task SaveLog(Window? owner)
        {
            if (owner is null || Rows.Count == 0)
                return;

            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"tv-console-{_app.TizenId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[] { new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } } },
            });
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, Encoding.UTF8);
            await writer.WriteAsync(Transcript());
        }

        [RelayCommand]
        private void ClearLog() => Rows.Clear();

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();

        private string Transcript()
        {
            var transcript = new StringBuilder();
            transcript.AppendLine($"{_app.DisplayName} ({_app.TizenId}) on {_tvIp}, inspector at 127.0.0.1:{_localPort}");
            transcript.AppendLine();
            foreach (var row in Rows)
                transcript.AppendLine(row.AsTextLine());
            return transcript.ToString();
        }

        private enum ConsoleStatus { Attaching, Attached, Detached }

        private void SetStatus(ConsoleStatus status)
        {
            (StatusText, StatusBrush) = status switch
            {
                ConsoleStatus.Attaching => (string.Format("statusDebugAttaching".Localized(), _app.DisplayName), AttachingBrush),
                ConsoleStatus.Attached => ("statusDebugAttached".Localized(), AttachedBrush),
                _ => ("statusDebugDetached".Localized(), DetachedBrush),
            };
        }
    }

    /// <summary>One console line, shaped for the row template.</summary>
    public sealed class ConsoleRowViewModel
    {
        private readonly ConsoleEntry _entry;

        public ConsoleRowViewModel(ConsoleEntry entry) => _entry = entry;

        public string Clock => _entry.Timestamp.ToString("HH:mm:ss");
        public string Text => _entry.Text;
        public string? Origin => _entry.Origin;
        public bool HasOrigin => !string.IsNullOrEmpty(_entry.Origin);

        // Same level colours as the phone's console, tuned for the dark ground the log sits on.
        public IBrush Brush => _entry.Level switch
        {
            ConsoleLevel.Error => ErrorBrush,
            ConsoleLevel.Warning => WarningBrush,
            ConsoleLevel.Info => InfoBrush,
            ConsoleLevel.Debug => DebugBrush,
            _ => LogBrush,
        };

        public string AsTextLine()
        {
            var level = _entry.Level.ToString().ToUpperInvariant();
            var origin = HasOrigin ? $"   ({_entry.Origin})" : string.Empty;
            return $"{Clock} {level,-7} {_entry.Text}{origin}";
        }

        private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E6B860"));
        private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#6FC3DF"));
        private static readonly IBrush DebugBrush = new SolidColorBrush(Color.Parse("#8E99A4"));
        private static readonly IBrush LogBrush = new SolidColorBrush(Color.Parse("#DCE3EA"));
    }
}
