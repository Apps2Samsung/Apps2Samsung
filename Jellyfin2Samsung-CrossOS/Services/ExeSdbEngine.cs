using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
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

        // Transient SDB transport hiccups: the TV drops the connection mid-read ("...forcibly closed by
        // the remote host", "Remote closed stream while reading"). These are races on the SDB link, not
        // real failures — a quick retry almost always succeeds. This is why a single reset on the
        // device-info read produced a spurious "TV Name could not be found" even though the TV was just
        // discovered (#524). Applied ONLY to idempotent read/query commands — never to
        // install/uninstall/resign, which aren't safe to blindly repeat.
        private static readonly string[] TransientTransportErrors =
        {
            "forcibly closed by the remote host",
            "Remote closed stream while reading",
            "Unable to read data from the transport connection",
            "Connection reset by peer",
        };

        private static bool IsTransientTransportError(ProcessResult result) =>
            !string.IsNullOrEmpty(result.Output) &&
            Array.Exists(TransientTransportErrors,
                marker => result.Output.Contains(marker, StringComparison.OrdinalIgnoreCase));

        private async Task<ProcessResult> RunWithRetry(string arguments, int attempts = 3)
        {
            var result = await _processHelper.RunCommandAsync(SdbPath, arguments);
            for (int i = 1; i < attempts && IsTransientTransportError(result); i++)
            {
                await Task.Delay(400 * i); // brief backoff before retrying the reset
                result = await _processHelper.RunCommandAsync(SdbPath, arguments);
            }
            return result;
        }

        public Task<ProcessResult> DevicesAsync(string tvIpAddress) => RunWithRetry($"devices {tvIpAddress}");

        public Task<ProcessResult> DisconnectAsync(string tvIpAddress) => Run($"disconnect {tvIpAddress}");

        public Task<ProcessResult> CapabilityAsync(string tvIpAddress) => RunWithRetry($"capability {tvIpAddress}");

        public Task<ProcessResult> DuidAsync(string tvIpAddress) => RunWithRetry($"duid {tvIpAddress}");

        public Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => RunWithRetry($"diagnose {tvIpAddress}");

        public Task<ProcessResult> AppsAsync(string tvIpAddress) => RunWithRetry($"apps {tvIpAddress}");

        public Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => Run($"launch {tvIpAddress} \"{appId}\"");

        public Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
            => Run($"resign \"{packagePath}\" \"{authorP12}\" \"{distributorP12}\" {certPass}");

        public Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath)
            => Run($"install {tvIpAddress} \"{packagePath}\" {sdkToolPath}");

        public Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId)
            => Run($"uninstall {tvIpAddress} {packageId}");

        public Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath)
            => Run($"permit-install {tvIpAddress} \"{deviceXml}\" {sdkToolPath}");

        public Task<ProcessResult> ShellAsync(string tvIpAddress, string command)
            => Run($"shell {tvIpAddress} {command}");

        public Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = SdbPath,
                    Arguments = $"forward {tvIpAddress} tcp:{localPort} tcp:{remotePort}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            return Task.FromResult<IAsyncDisposable>(new ProcessForwardSession(process));
        }

        private class ProcessForwardSession : IAsyncDisposable
        {
            private readonly Process _process;

            public ProcessForwardSession(Process process)
            {
                _process = process;
            }

            public ValueTask DisposeAsync()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill();
                    }
                    _process.Dispose();
                }
                catch { }
                return default;
            }
        }
    }
}
