using Apps2Samsung.Models;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    /// <summary>
    /// Abstraction over the Tizen SDB engine — the layer that actually talks to the TV over the
    /// developer protocol (connect/install/uninstall/resign/…). The desktop head implements this
    /// by shelling out to the downloaded <c>TizenSdb.exe</c>; a mobile head implements it by
    /// calling <c>TizenSdb.Core</c> in-process. Each method returns the raw <see cref="ProcessResult"/>
    /// so the orchestration/output-parsing above it stays engine-agnostic.
    /// </summary>
    public interface ISdbEngine
    {
        /// <summary>Lists the connected device (used to read the TV's friendly name).</summary>
        Task<ProcessResult> DevicesAsync(string tvIpAddress);

        /// <summary>Drops the SDB connection to the TV.</summary>
        Task<ProcessResult> DisconnectAsync(string tvIpAddress);

        /// <summary>Queries the device capability report (platform version, SDK tool path, …).</summary>
        Task<ProcessResult> CapabilityAsync(string tvIpAddress);

        /// <summary>Reads the TV's DUID (needed for the distributor certificate).</summary>
        Task<ProcessResult> DuidAsync(string tvIpAddress);

        /// <summary>Runs the engine's self-diagnostic against the TV.</summary>
        Task<ProcessResult> DiagnoseAsync(string tvIpAddress);

        /// <summary>Lists the apps installed on the TV.</summary>
        Task<ProcessResult> AppsAsync(string tvIpAddress);

        /// <summary>Launches an installed app by id.</summary>
        Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId);

        /// <summary>Re-signs a .wgt with the given author/distributor PKCS#12 certificates.</summary>
        Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass);

        /// <summary>Pushes and installs a .wgt onto the TV.</summary>
        Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath);

        /// <summary>Uninstalls an app from the TV by package id.</summary>
        Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId);

        /// <summary>Pushes the distributor device-profile XML that permits sideloading.</summary>
        Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath);
    }
}
