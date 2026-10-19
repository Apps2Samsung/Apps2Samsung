using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Apps2Samsung.Diagnostics
{
    public enum LogEntryLevel
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>One entry of a debug log: its time, a guessed level, and the message (with any
    /// continuation lines, e.g. a stack trace, folded in).</summary>
    public sealed record LogEntry(DateTime? Time, LogEntryLevel Level, string Message);

    /// <summary>One session's log file, newest first in <see cref="LogReader.Sessions"/>.</summary>
    public sealed record LogSession(string Path, DateTime Started, long Bytes, bool IsCurrent);

    /// <summary>
    /// Reads the files <see cref="FileLog"/> writes, for the log viewer on both heads (#755). The
    /// lines carry no level, so it is guessed from the wording: the viewer's Warnings / Errors filters
    /// are a reading aid, not a promise that nothing else went wrong.
    /// </summary>
    public static class LogReader
    {
        private const string FilePrefix = "debug_";
        private const string FileTimeFormat = "yyyy-MM-dd_HH-mm-ss-fff";
        private const string LineTimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private static readonly Regex ErrorWords = new(
            @"\b(fatal|exception|error|errors|failed|failure|denied|refused)\b|install failed\[",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex WarningWords = new(
            @"\b(warn|warning|retry|retrying|timeout|timed out|offline|mismatch|not found|skipped|couldn't|can't|cannot)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Every session log in <paramref name="directory"/>, newest first.</summary>
        public static IReadOnlyList<LogSession> Sessions(string? directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return Array.Empty<LogSession>();

            var current = FileLog.CurrentLogFile;
            return Directory.EnumerateFiles(directory, FilePrefix + "*.log")
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    var stamp = System.IO.Path.GetFileNameWithoutExtension(path).Substring(FilePrefix.Length);
                    var started = DateTime.TryParseExact(stamp, FileTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                        ? t
                        : info.CreationTime;
                    var isCurrent = current is not null && string.Equals(
                        System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
                    return new LogSession(path, started, info.Length, isCurrent);
                })
                .OrderByDescending(s => s.Started)
                .ToList();
        }

        /// <summary>
        /// The entries of <paramref name="path"/>. Opened with shared read/write, since the current
        /// session's file is still being appended to. A missing or unreadable file gives no entries.
        /// </summary>
        public static IReadOnlyList<LogEntry> Read(string path)
        {
            string text;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException) { return Array.Empty<LogEntry>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<LogEntry>(); }

            return Parse(text);
        }

        /// <summary>Splits log text into entries: a line starting with a timestamp opens one, any
        /// other line belongs to the entry before it.</summary>
        public static IReadOnlyList<LogEntry> Parse(string text)
        {
            var entries = new List<LogEntry>();
            DateTime? time = null;
            var message = new List<string>();

            void Flush()
            {
                if (message.Count == 0)
                    return;
                var joined = string.Join("\n", message);
                entries.Add(new LogEntry(time, Classify(joined), joined));
                message.Clear();
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length >= LineTimeFormat.Length &&
                    DateTime.TryParseExact(line.Substring(0, LineTimeFormat.Length), LineTimeFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                {
                    Flush();
                    time = stamp;
                    message.Add(line.Substring(LineTimeFormat.Length).TrimStart());
                }
                else if (line.Length > 0)
                {
                    message.Add(line);
                }
            }
            Flush();
            return entries;
        }

        /// <summary>The level a message reads as. Only the first line counts, so a stack trace under
        /// a warning doesn't turn it into an error on its own.</summary>
        public static LogEntryLevel Classify(string message)
        {
            var firstLine = message.Split('\n')[0];
            if (ErrorWords.IsMatch(firstLine))
                return LogEntryLevel.Error;
            if (WarningWords.IsMatch(firstLine))
                return LogEntryLevel.Warning;
            return LogEntryLevel.Info;
        }

        /// <summary>Entries that pass the viewer's filter and search.</summary>
        public static IEnumerable<LogEntry> Filter(IEnumerable<LogEntry> entries, LogEntryLevel minimum, string? search)
        {
            var query = entries.Where(e => e.Level >= minimum);
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(e => e.Message.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
            return query;
        }

        /// <summary>Entries back as plain text, for Copy.</summary>
        public static string ToText(IEnumerable<LogEntry> entries) =>
            string.Join(Environment.NewLine, entries.Select(e =>
                e.Time is { } t ? $"{t.ToString(LineTimeFormat, CultureInfo.InvariantCulture)} {e.Message}" : e.Message));
    }
}
