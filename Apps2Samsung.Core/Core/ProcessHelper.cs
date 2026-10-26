using System;
using System.Diagnostics;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Clears leftover Tizen SDK <c>sdb</c> server processes. The app itself no longer shells out to
    /// any SDB binary — it drives TizenSdb.Core in-process (#549) — but a stray sdb server from the
    /// official SDK can still hold a TV's single debug connection and make our connect fail. Runs
    /// at startup and, through <see cref="Apps2Samsung.Sdb.InProcessSdbEngine.ClearCompetingSdbServer"/>,
    /// before every fresh connect: Tizen Studio's Device Manager and Emulator Manager restart the
    /// server within seconds, so a single sweep at startup did not cover a connection made later.
    /// In Core so the desktop and the CLI share it; the phone has no sdb server to clear.
    /// </summary>
    public static class ProcessHelper
    {
        /// <returns>True when at least one sdb server was found and stopped.</returns>
        public static bool KillSdbServers()
        {
            try
            {
                Process[] sdbProcesses = Process.GetProcessesByName("sdb");

                if (sdbProcesses.Length == 0)
                    return false;

                foreach (Process proc in sdbProcesses)
                {
                    proc.Kill();
                    proc.WaitForExit();
                    Trace.WriteLine($"Killed SDB {proc.Id} - {proc.ProcessName}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to stop SDB server: {ex}");
                return false;
            }
        }
    }
}
