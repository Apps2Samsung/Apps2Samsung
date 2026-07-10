using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Desktop <see cref="ISdbEngine"/>: shells out to the downloaded <c>TizenSdb.exe</c> via
    /// <see cref="ProcessHelper"/>. The exe path is supplied by a provider — it's owned and
    /// resolved by <c>TizenInstallerService.EnsureTizenSdbAvailable()</c>, which downloads/updates
    /// the binary before any install runs — so this engine stays a thin CLI-argument builder.
    /// This is also the one place that knows the exe's command-line contract; a mobile head would
    /// instead implement <see cref="ISdbEngine"/> against <c>TizenSdb.Core</c> directly.
    /// </summary>
    public class ExeSdbEngine : ISdbEngine
    {
        private readonly ProcessHelper _processHelper;
        private readonly Func<string?> _sdbPathProvider;

        public ExeSdbEngine(ProcessHelper processHelper, Func<string?> sdbPathProvider)
        {
            _processHelper = processHelper;
            _sdbPathProvider = sdbPathProvider;
        }

        private string SdbPath => _sdbPathProvider()
            ?? throw new InvalidOperationException(
                "Tizen SDB path not resolved. EnsureTizenSdbAvailable() must run before any SDB command.");

        private Task<ProcessResult> Run(string arguments) => _processHelper.RunCommandAsync(SdbPath, arguments);

        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => Run($"devices {tvIpAddress}");

        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => Run($"disconnect {tvIpAddress}");

        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => Run($"capability {tvIpAddress}");

        public Task<ProcessResult> DuidAsync(string tvIpAddress) => Run($"duid {tvIpAddress}");

        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => Run($"diagnose {tvIpAddress}");

        public Task<ProcessResult> AppsAsync(string tvIpAddress) => Run($"apps {tvIpAddress}");

        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => Run($"launch {tvIpAddress} \"{appId}\"");

        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
            => Run($"resign \"{packagePath}\" \"{authorP12}\" \"{distributorP12}\" {certPass}");

        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath)
            => Run($"install {tvIpAddress} \"{packagePath}\" {sdkToolPath}");

        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId)
            => Run($"uninstall {tvIpAddress} {packageId}");

        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath)
            => Run($"permit-install {tvIpAddress} \"{deviceXml}\" {sdkToolPath}");
    }
}
