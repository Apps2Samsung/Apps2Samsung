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
            // Route Trace to a file BEFORE Avalonia starts (shared with the mobile head via Core).
            Apps2Samsung.Diagnostics.FileLog.Initialize(Path.Combine(AppContext.BaseDirectory, "Logs"));


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