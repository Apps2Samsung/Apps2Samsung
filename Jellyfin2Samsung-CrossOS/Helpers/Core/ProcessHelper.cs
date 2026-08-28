using System;
using System.Diagnostics;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Clears leftover Tizen SDK <c>sdb</c> server processes. The app itself no longer shells out to
    /// any SDB binary — it drives TizenSdb.Core in-process (#549) — but a stray sdb server from the
    /// official SDK can still hold a TV's single debug connection and make our connect fail.
    /// </summary>
    public static class ProcessHelper
    {
        public static void KillSdbServers()
        {
            try
            {
                Process[] sdbProcesses = Process.GetProcessesByName("sdb");

                if (sdbProcesses.Length == 0)
                    return;

                foreach (Process proc in sdbProcesses)
                {
                    proc.Kill();
                    proc.WaitForExit();
                    Trace.WriteLine($"Killed SDB {proc.Id} - {proc.ProcessName}");
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to stop SDB server: {ex}");
            }
        }
    }
}
