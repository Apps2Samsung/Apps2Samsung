using System.Net;
using System.Net.Sockets;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// Picks the local end of an SDB tunnel. Every tunnel — the web inspector, a packaged service's
    /// HTTP endpoint — needs a port on this device that nothing else is listening on.
    /// </summary>
    public static class LocalPorts
    {
        /// <summary>
        /// Asks the OS for an unused port by binding port 0 and reading back what it assigned. There is
        /// an unavoidable race between releasing it here and the tunnel claiming it, but the
        /// alternative — a hardcoded port — collides far more often on a phone, where nothing
        /// guarantees a well-known debug port is free.
        /// </summary>
        public static int FindFree()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
