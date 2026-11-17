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

            // An install link (#1066): handed to the Apps2Samsung that already runs, if one does;
            // else this start opens it once the catalog is in, and takes later links itself.
            var link = Apps2Samsung.Services.InstallLinkRelay.FromArgs(args);
            if (link is not null && Apps2Samsung.Services.InstallLinkRelay.TrySend(link))
                return;
            if (link is not null)
                Apps2Samsung.Services.InstallLinkRelay.Deliver(link);
            Apps2Samsung.Services.InstallLinkRelay.StartListening();
            Apps2Samsung.Services.UrlSchemeRegistration.EnsureRegistered();


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