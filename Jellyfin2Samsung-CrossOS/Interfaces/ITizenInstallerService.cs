using Apps2Samsung.Extensions;
using Apps2Samsung.Models;
using System;
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
        Task<InstallResult> InstallPackageAsync(string packageUrl, string tvIpAddress, CancellationToken cancellationToken, ProgressCallback? progress = null, Action? onSamsungLoginStarted = null);
    }
}
