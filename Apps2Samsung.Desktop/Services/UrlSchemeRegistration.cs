using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Registers apps2samsung:// for this Windows user (#1066), so a browser hands install links to
    /// this copy of Apps2Samsung. Per user (HKCU), no admin needed, and done on every start so a copy
    /// that moved (a new portable folder, an update to another path) takes the links from then on.
    /// macOS registers through Info.plist and Linux through the .desktop file of its package.
    /// </summary>
    public static class UrlSchemeRegistration
    {
        public static void EnsureRegistered()
        {
            if (!OperatingSystem.IsWindows())
                return;
            try
            {
                RegisterWindows();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[links] could not register apps2samsung:// : {ex.Message}");
            }
        }

        [SupportedOSPlatform("windows")]
        private static void RegisterWindows()
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return;
            var command = $"\"{exe}\" \"%1\"";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\apps2samsung");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            if (string.Equals(cmd.GetValue(null) as string, command, StringComparison.Ordinal))
                return;
            key.SetValue(null, "URL:Apps2Samsung install link");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exe}\",0");
            cmd.SetValue(null, command);
            Trace.WriteLine("[links] registered apps2samsung:// for this user");
        }
    }
}
