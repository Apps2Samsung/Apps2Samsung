using System;
using System.Diagnostics;
using System.IO;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>
    /// A <see cref="TraceListener"/> that appends every <see cref="Trace"/> line to a file, prefixed
    /// with a timestamp. Shared by both heads via <see cref="FileLog"/> so diagnostics are persisted
    /// off-device for debugging (on mobile this is what makes the app debuggable at all).
    /// </summary>
    public sealed class FileTraceListener : TraceListener
    {
        private readonly string _filePath;
        private readonly object _gate = new();

        public FileTraceListener(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        }

        public override void Write(string? message)
        {
            if (message == null) return;
            lock (_gate) File.AppendAllText(_filePath, message);
        }

        public override void WriteLine(string? message)
        {
            if (message == null) return;
            lock (_gate) File.AppendAllText(
                _filePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
