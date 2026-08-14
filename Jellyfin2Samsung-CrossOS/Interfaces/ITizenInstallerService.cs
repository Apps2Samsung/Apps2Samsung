using Apps2Samsung.Extensions;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface ITizenInstallerService
    {
        /// <summary>
        /// Path to the resolved TizenSdb executable, set by <see cref="EnsureTizenSdbAvailable"/>.
        /// Null until that has run. Consumed by the desktop <c>ExeSdbEngine</c> to locate the binary.
        /// </summary>
        string? TizenSdbPath { get; }

        Task<string> GetTvNameAsync(string tvIpAddress);
        Task<string> EnsureTizenSdbAvailable();
        Task<string> DownloadPackageAsync(string downloadUrl, bool validateWgt = false);
        Task<InstallResult> InstallPackageAsync(string packageUrl, string tvIpAddress, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null, bool? wasAlreadyInstalled = null);

        /// <summary>Lists the apps installed on the TV (ensures the SDB binary, queries, parses).</summary>
        Task<IReadOnlyList<Apps2Samsung.Models.InstalledApp>> GetInstalledAppsAsync(string tvIpAddress);

        /// <summary>Uninstalls an app from the TV by its Tizen id. Returns the raw SDB result.</summary>
        Task<Apps2Samsung.Models.ProcessResult> UninstallAppAsync(string tvIpAddress, string tizenId);

        /// <summary>Gathers the TV's details (DUID, Tizen version, developer mode/IP, …) for the
        /// "TV information" view, using the shared Core gatherer.</summary>
        Task<Apps2Samsung.Models.TizenDeviceInfo> GetDeviceInfoAsync(string tvIpAddress, bool debugPortOpen);
    }
}
