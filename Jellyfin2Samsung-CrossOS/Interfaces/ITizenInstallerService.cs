using Apps2Samsung.Extensions;
using Apps2Samsung.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Interfaces
{
    public interface ITizenInstallerService
    {
        Task<string> GetTvNameAsync(string tvIpAddress);
        Task<string> EnsureTizenSdbAvailable();
        Task<string> DownloadPackageAsync(string downloadUrl);
        Task<InstallResult> InstallPackageAsync(string packageUrl, string tvIpAddress, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null);
    }
}
