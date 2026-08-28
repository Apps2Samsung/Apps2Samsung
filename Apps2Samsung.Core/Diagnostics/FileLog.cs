using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Diagnostics
{
    /// <summary>
    /// Shared file logging. Routes <see cref="Trace"/> output to
    /// <c>&lt;logDirectory&gt;/debug_&lt;timestamp&gt;.log</c>. Both heads call
    /// <see cref="Initialize"/> once at startup — the desktop next to the binary, the mobile head in
    /// its app-data dir — so the Core services' Trace diagnostics are persisted and the mobile app is
    /// actually debuggable (it previously logged only to the transient Android debug console).
    ///
    /// Initialize also (a) tees <see cref="Console"/> stdout/stderr into the log, so output from lower
    /// layers and third-party libraries (e.g. the in-process SDB engine) is captured off-device, and
    /// (b) logs otherwise-unhandled exceptions. Combined with the heads routing their UI status +
    /// caught exceptions through Trace, this makes the log a full session transcript.
    /// </summary>
    public static class FileLog
    {
        private static bool _initialized;

        /// <summary>The debug log file created by <see cref="Initialize"/> (null until initialized).</summary>
        public static string? CurrentLogFile { get; private set; }

        /// <summary>
        /// The platform-appropriate log directory shared by every desktop log writer (Program startup
        /// and the "Open logs folder" buttons), so they can never drift apart.
        ///
        /// Not next to the binary on macOS or Linux: writing inside the ad-hoc-signed .app bundle
        /// breaks static signature validation (#498), and a Linux package can be installed read-only —
        /// root-owned under /opt, or a squashfs mount in an AppImage, where this would silently produce
        /// no logs at all (#589). Windows keeps logs next to the binary, where existing installs have
        /// them. The mobile head passes <c>FileSystem.AppDataDirectory</c> and never uses this.
        /// </summary>
        public static string DefaultLogDirectory =>
            Portability.UserPaths.State(AppContext.BaseDirectory, "Logs");

        /// <summary>Adds a file trace listener under <paramref name="logDirectory"/>, tees Console into
        /// it, and logs unhandled exceptions. Idempotent and never throws — logging must not take down
        /// startup.</summary>
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

                CaptureConsole();
                HookGlobalExceptions();

                Trace.WriteLine($"[FileLog] Logging started → {CurrentLogFile}");
            }
            catch { /* diagnostics must never crash the app */ }
        }

        /// <summary>Writes a line to the log via Trace. A convenience for heads/pages so they don't each
        /// need a <c>using System.Diagnostics</c>; identical to <c>Trace.WriteLine</c>.</summary>
        public static void Write(string message) => Trace.WriteLine(message);

        // Tee Console.Out/Error through Trace so anything printed to stdout/stderr by any layer lands
        // in the log too (while still reaching the original console / logcat).
        private static bool _consoleCaptured;
        private static void CaptureConsole()
        {
            if (_consoleCaptured) return;
            _consoleCaptured = true;
            try
            {
                Console.SetOut(new TraceTextWriter(Console.Out, "out"));
                Console.SetError(new TraceTextWriter(Console.Error, "err"));
            }
            catch { /* console may be unavailable on some hosts */ }
        }

        // Last-resort capture: exceptions that escape the app still get a line in the log.
        private static bool _exceptionsHooked;
        private static void HookGlobalExceptions()
        {
            if (_exceptionsHooked) return;
            _exceptionsHooked = true;
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                    Trace.WriteLine($"[FATAL] Unhandled exception: {e.ExceptionObject}");
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    Trace.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
                    e.SetObserved();
                };
            }
            catch { /* best-effort */ }
        }

        // Forwards Console writes to the original writer AND mirrors full lines to Trace (so they hit
        // the file listener). Partial writes still reach the original console. Never recurses: the
        // Trace file listener doesn't write to Console.
        private sealed class TraceTextWriter : TextWriter
        {
            private readonly TextWriter _inner;
            private readonly string _tag;

            public TraceTextWriter(TextWriter inner, string tag)
            {
                _inner = inner;
                _tag = tag;
            }

            public override Encoding Encoding => _inner.Encoding;

            public override void Write(char value) => _inner.Write(value);
            public override void Write(string? value) => _inner.Write(value);

            public override void WriteLine(string? value)
            {
                _inner.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                    Trace.WriteLine($"[console:{_tag}] {value}");
            }
        }
    }
}
