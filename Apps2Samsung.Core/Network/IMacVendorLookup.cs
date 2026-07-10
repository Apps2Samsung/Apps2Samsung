using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Resolves a device's manufacturer from its IP, for cosmetically enriching a found TV's
    /// name during a scan. Platform-specific and entirely optional: the desktop implementation
    /// reads the ARP table (<c>arp</c>) and queries macvendors.com; a head with no ARP access
    /// (e.g. Android) simply doesn't register one, and TV detection — which relies only on the
    /// open debug/REST ports — is unaffected.
    /// </summary>
    public interface IMacVendorLookup
    {
        Task<string?> GetManufacturerFromIpAsync(string ipAddress);
    }
}
