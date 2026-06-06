using Avalonia;
using Apps2Samsung.Extensions;
using System;
using System.Diagnostics;
using System.IO;

namespace Apps2Samsung
{
    internal sealed class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Register trace listener BEFORE Avalonia starts
            var logDir = AppContext.BaseDirectory;
            string logFolder = Path.Combine(logDir, "Logs");
            Directory.CreateDirectory(logFolder);
            var dtg = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            var logFile = Path.Combine(logFolder, $"debug_{dtg}.log");

            Trace.Listeners.Add(new FileTraceListener(logFile));
            Trace.AutoFlush = true;


            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}