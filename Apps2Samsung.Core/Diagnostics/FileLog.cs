using System;
using System.Diagnostics;
using System.IO;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>
    /// Shared file logging. Routes <see cref="Trace"/> output to
    /// <c>&lt;logDirectory&gt;/debug_&lt;timestamp&gt;.log</c>. Both heads call
    /// <see cref="Initialize"/> once at startup — the desktop next to the binary, the mobile head in
    /// its app-data dir — so the Core services' Trace diagnostics are persisted and the mobile app is
    /// actually debuggable (it previously logged only to the transient Android debug console).
    /// </summary>
    public static class FileLog
    {
        private static bool _initialized;

        /// <summary>The debug log file created by <see cref="Initialize"/> (null until initialized).</summary>
        public static string? CurrentLogFile { get; private set; }

        /// <summary>Adds a file trace listener under <paramref name="logDirectory"/>. Idempotent and
        /// never throws — logging must not take down startup.</summary>
        public static void Initialize(string logDirectory)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                Directory.CreateDirectory(logDirectory);
                var dtg = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
                CurrentLogFile = Path.Combine(logDirectory, $"debug_{dtg}.log");
                Trace.Listeners.Add(new FileTraceListener(CurrentLogFile));
                Trace.AutoFlush = true;
            }
            catch { /* diagnostics must never crash the app */ }
        }
    }
}
