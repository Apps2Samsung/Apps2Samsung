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
            // DefaultLogDirectory keeps logs OUT of the .app bundle on macOS (see FileLog / issue #498).
            Apps2Samsung.Diagnostics.FileLog.Initialize(Apps2Samsung.Diagnostics.FileLog.DefaultLogDirectory);


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