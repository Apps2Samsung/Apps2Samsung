using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Diagnostics;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apps2Samsung.ViewModels
{
    /// <summary>One log entry as the viewer's table shows it.</summary>
    public sealed record LogViewerRow(string Time, string Level, string Message, IBrush Ink, IBrush Back);

    /// <summary>A session in the viewer's picker.</summary>
    public sealed record LogSessionOption(LogSession Session, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>
    /// The debug log viewer (#755): a session picked from the log folder, an All / Warnings / Errors
    /// filter and a search over <see cref="LogReader"/>'s entries, Copy, Save as and Open folder.
    /// The current session is re-read while the window is open (<see cref="FollowAsync"/>).
    /// </summary>
    public partial class LogViewerViewModel : ObservableObject
    {
        private readonly string _directory;
        private readonly Func<string, string> _localize;
        private readonly Func<string, IBrush> _brush;
        private IReadOnlyList<LogEntry> _entries = Array.Empty<LogEntry>();
        private long _readLength;

        public LogViewerViewModel(string directory, Func<string, string> localize, Func<string, IBrush> brush)
        {
            _directory = directory;
            _localize = localize;
            _brush = brush;
            TitleText = localize("lblDebugLog");
            AllText = localize("lblLogAll");
            WarningsText = localize("lblLogWarnings");
            ErrorsText = localize("lblLogErrors");
            SearchText = localize("lblSearchLog");
            TimeHeader = localize("lblLogTime");
            LevelHeader = localize("lblLogLevel");
            MessageHeader = localize("lblLogMessage");
            OpenFolderText = localize("lblOpenLogsFolder");
            SaveAsText = localize("lblSaveAs");
            EmptyText = localize("lblLogNothingMatches");
            copyText = localize("lblCopy");

            foreach (var session in LogReader.Sessions(directory))
                Sessions.Add(new LogSessionOption(session, session.IsCurrent
                    ? localize("lblLogThisSession")
                    : string.Format(localize("lblLogSessionOf"), session.Started.ToString("g", CultureInfo.CurrentCulture))));
            selectedSession = Sessions.FirstOrDefault();
            Load();
        }

        public string TitleText { get; }
        public string AllText { get; }
        public string WarningsText { get; }
        public string ErrorsText { get; }
        public string SearchText { get; }
        public string TimeHeader { get; }
        public string LevelHeader { get; }
        public string MessageHeader { get; }
        public string OpenFolderText { get; }
        public string SaveAsText { get; }
        public string EmptyText { get; }
        public string Directory => _directory;

        public ObservableCollection<LogSessionOption> Sessions { get; } = new();
        public ObservableCollection<LogViewerRow> Rows { get; } = new();

        [ObservableProperty] private LogSessionOption? selectedSession;
        [ObservableProperty] private string? search;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowWarnings), nameof(ShowErrors), nameof(ShowAll))] private LogEntryLevel minimum;
        [ObservableProperty] private string footText = string.Empty;
        [ObservableProperty] private string copyText;
        [ObservableProperty] private bool isEmpty;

        public bool ShowAll { get => Minimum == LogEntryLevel.Info; set { if (value) Minimum = LogEntryLevel.Info; } }
        public bool ShowWarnings { get => Minimum == LogEntryLevel.Warning; set { if (value) Minimum = LogEntryLevel.Warning; } }
        public bool ShowErrors { get => Minimum == LogEntryLevel.Error; set { if (value) Minimum = LogEntryLevel.Error; } }

        /// <summary>The shown entries as text, for Copy / Save as.</summary>
        public string ShownText => LogReader.ToText(LogReader.Filter(_entries, Minimum, Search));

        /// <summary>The selected session's file; Save as copies it whole.</summary>
        public string? SelectedPath => SelectedSession?.Session.Path;

        partial void OnSelectedSessionChanged(LogSessionOption? value) => Load();
        partial void OnSearchChanged(string? value) => Render();
        partial void OnMinimumChanged(LogEntryLevel value) => Render();

        /// <summary>Re-reads the current session when it has grown since the last read.</summary>
        public void Follow()
        {
            if (SelectedSession is not { Session.IsCurrent: true } current)
                return;
            long length;
            try { length = new FileInfo(current.Session.Path).Length; }
            catch (IOException) { return; }
            if (length != _readLength)
                Load();
        }

        public void MarkCopied() => CopyText = _localize("lblCopied");

        private void Load()
        {
            var session = SelectedSession?.Session;
            _entries = session is null ? Array.Empty<LogEntry>() : LogReader.Read(session.Path);
            try { _readLength = session is null ? 0 : new FileInfo(session.Path).Length; }
            catch (IOException) { _readLength = 0; }
            Render();
        }

        private void Render()
        {
            Rows.Clear();
            foreach (var entry in LogReader.Filter(_entries, Minimum, Search))
                Rows.Add(ToRow(entry));
            IsEmpty = Rows.Count == 0;
            CopyText = _localize("lblCopy");

            var session = SelectedSession?.Session;
            FootText = session is null
                ? _localize("statusNoLogFile")
                : $"{session.Path} · {string.Format(_localize("lblLogLineCount"), _entries.Count)}";
        }

        private LogViewerRow ToRow(LogEntry entry)
        {
            var time = entry.Time?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
            return entry.Level switch
            {
                LogEntryLevel.Error => new LogViewerRow(time, _localize("lblLogLevelError"), entry.Message, _brush("Bad"), _brush("BadSoft")),
                LogEntryLevel.Warning => new LogViewerRow(time, _localize("lblLogLevelWarning"), entry.Message, _brush("Warn"), _brush("WarnSoft")),
                _ => new LogViewerRow(time, _localize("lblLogLevelInfo"), entry.Message, _brush("Ink"), Brushes.Transparent),
            };
        }
    }
}
