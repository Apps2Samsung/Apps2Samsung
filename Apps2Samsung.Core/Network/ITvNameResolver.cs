using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Resolves a Samsung TV's display name from its IP. Backed by the SDB engine (the desktop
    /// head shells out to <c>TizenSdb.exe</c>; a mobile head would call the engine in-process),
    /// so it's abstracted here to keep <see cref="INetworkService"/> free of the SDB-invoke layer.
    /// Optional during a scan: when absent, found devices simply carry no friendly name.
    /// </summary>
    public interface ITvNameResolver
    {
        Task<string> GetTvNameAsync(string tvIpAddress);
    }
}
