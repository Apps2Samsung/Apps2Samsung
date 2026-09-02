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
        Task<string> GetTvNameAsync(string tvIpAddress);
        Task<string> DownloadPackageAsync(string downloadUrl);
        Task<InstallResult> InstallPackageAsync(string packageUrl, string tvIpAddress, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null, bool? wasAlreadyInstalled = null);

        /// <summary>Lists the apps installed on the TV (queries the TV and parses the reply).</summary>
        Task<IReadOnlyList<Apps2Samsung.Models.InstalledApp>> GetInstalledAppsAsync(string tvIpAddress);

        /// <summary>Uninstalls an app from the TV by its Tizen id. Returns the raw SDB result.</summary>
        Task<Apps2Samsung.Models.ProcessResult> UninstallAppAsync(string tvIpAddress, string tizenId);

        /// <summary>Gathers the TV's details (DUID, Tizen version, developer mode/IP, …) for the
        /// "TV information" view, using the shared Core gatherer.</summary>
        Task<Apps2Samsung.Models.TizenDeviceInfo> GetDeviceInfoAsync(string tvIpAddress, bool debugPortOpen);

        Task LaunchAppAsync(string tvIpAddress, string tizenId);
        Task StopAppAsync(string tvIpAddress, string tizenId);
        Task<(int LocalPort, IAsyncDisposable ForwardSession)> DebugAppAsync(string tvIpAddress, string tizenId);
    }
}
