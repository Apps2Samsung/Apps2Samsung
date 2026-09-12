using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using TizenSdb;
using TizenSdb.SdbClient;
using TizenSdb.SigningManager;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// The <see cref="ISdbEngine"/> both heads run on: drives <c>TizenSdb.Core</c> in-process, so
    /// there is no external binary to download, update or ship (#549).
    /// <para>
    /// One <see cref="SdbTcpDevice"/> connection is kept per TV and reused across sequential calls to
    /// that TV rather than reconnecting per command — the reconnect churn is what makes Samsung's sdbd
    /// close a fresh connection mid-handshake. Each TV has its own gate, so the desktop's network scan
    /// can resolve several TVs' names concurrently while calls to one TV stay serialized. A connection
    /// is dropped on any failure, so the next call reconnects on a fresh socket.
    /// </para>
    /// </summary>
    public sealed class InProcessSdbEngine : ISdbEngine, IAsyncDisposable
    {
        // How long a pooled connection may sit unused before the next call reconnects instead of
        // trusting it. Reuse exists to avoid back-to-back reconnect churn, which is a matter of
        // milliseconds; a connection idle for longer than this is likely already dead on the TV's
        // side (it sleeps, drops the link, or sdbd times it out). Without this, the first command
        // after the user pauses — e.g. picking a version between the network scan and the install —
        // would fail on a stale socket, and an install is not retried.
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        // Serializes the Console.Out redirect used to capture an install log (see RunCapturingDeviceLog).
        private static readonly SemaphoreSlim ConsoleCaptureGate = new(1, 1);

        private readonly ConcurrentDictionary<string, DeviceSlot> _slots = new();

        // Commands ListApps tries in order (first useful reply wins) — ported from the CLI.
        private static readonly string[] AppListCommands =
        {
            "0 vd_applist", "applist", "pkgcmd -l", "pm list packages", "ls /usr/apps", "ls /opt/usr/apps",
        };

        // Commands diagnose probes — ported from the CLI. The "0 vd_appuninstall test" line is what the
        // diagnose parser keys on, so its exact "Testing '…': SUCCESS/FAILED" format is preserved.
        private static readonly string[] DiagnoseCommands =
        {
            "0 getduid", "host:version", "host:features", "shell:uname -a", "shell:ls /usr/apps",
            "shell:pwd", "shell:whoami", "0 vd_applist", "0 vd_appuninstall test", "pkgcmd -l",
        };

        // Transient SDB transport hiccups: the TV drops the connection mid-read. These are races on the
        // SDB link, not real failures — a quick retry almost always succeeds. This is why a single reset
        // on the device-info read produced a spurious "TV Name could not be found" even though the TV
        // was just discovered (#524). Retried ONLY for the idempotent read/query commands below — never
        // for install/uninstall/resign, which aren't safe to blindly repeat.
        private static readonly string[] TransientTransportErrors =
        {
            "forcibly closed by the remote host",
            "Remote closed stream while reading",
            "Unable to read data from the transport connection",
            "Connection reset by peer",
        };

        public async Task<ProcessResult> DevicesAsync(string tvIpAddress) => await RunWithRetry(tvIpAddress, $"devices {tvIpAddress}", device =>
        {
            var parts = device.DeviceId.Split("::", StringSplitOptions.RemoveEmptyEntries);
            return Task.FromResult(parts.Length >= 2 ? parts[1] : string.Empty);
        });

        public async Task<ProcessResult> DisconnectAsync(string tvIpAddress)
        {
            // Drop the reused connection so the next call reconnects.
            if (_slots.TryGetValue(tvIpAddress, out var slot))
            {
                await slot.Gate.WaitAsync().ConfigureAwait(false);
                try { await slot.DropAsync(); }
                finally { slot.Gate.Release(); }
            }
            var result = Ok($"* Disconnected from {tvIpAddress}");
            Log($"disconnect {tvIpAddress}", result);
            return result;
        }

        public async Task<ProcessResult> CapabilityAsync(string tvIpAddress) => await RunWithRetry(tvIpAddress, $"capability {tvIpAddress}", async device =>
        {
            var caps = await device.CapabilityAsync();
            var sb = new StringBuilder();
            foreach (var cap in caps)
                sb.AppendLine($"  {cap.Key}: {cap.Value}");
            return sb.ToString();
        });

        public async Task<ProcessResult> DuidAsync(string tvIpAddress) => await RunWithRetry(tvIpAddress, $"duid {tvIpAddress}", async device =>
        {
            var duid = await device.ShellCommandAsync("0 getduid");
            return duid.Trim();
        });

        public async Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => await RunWithRetry(tvIpAddress, $"diagnose {tvIpAddress}", async device =>
        {
            var sb = new StringBuilder();
            foreach (var cmd in DiagnoseCommands)
            {
                try
                {
                    var result = await device.ShellCommandAsync(cmd);
                    sb.AppendLine($"  Testing '{cmd}': SUCCESS ({result.Length} chars)");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Testing '{cmd}': FAILED - {ex.Message}");
                }
            }

            // Which shell verbs this TV's sdbd recognises (TizenSdb.Core's SdbShellVerbs). sdbd is a
            // fixed vocabulary, not a shell, and the launcher verb the tooling uses only resolves Smart
            // Hub apps (tizen-community-packages#34); whether a set exposes a verb that reaches the
            // platform's own launcher is a question no log had answered. Every probe carries an id
            // that does not exist, so nothing on the TV changes. Appended after the "Testing" lines
            // so the diagnose parser, which keys on the vd_appuninstall line, is unaffected — and so a
            // user's debug log carries the answer without anyone asking for it.
            try
            {
                var probes = await device.ProbeShellVerbsAsync();
                sb.AppendLine($"  sdbd verbs: {probes.Count(p => p.Accepted)} of {probes.Count} probes accepted");
                foreach (var probe in probes)
                    sb.AppendLine(SdbShellVerbs.Format(probe));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  sdbd verbs: probe failed - {ex.Message}");
            }

            return sb.ToString();
        });

        public async Task<ProcessResult> AppsAsync(string tvIpAddress) => await RunWithRetry(tvIpAddress, $"apps {tvIpAddress}", async device =>
        {
            foreach (var cmd in AppListCommands)
            {
                try
                {
                    var result = await device.ShellCommandAsync(cmd);
                    if (!string.IsNullOrEmpty(result) && !result.Contains("not found") && !result.Contains("No such"))
                    {
                        var sb = new StringBuilder();
                        foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = line.Trim();
                            if (trimmed.Length > 1)
                                sb.AppendLine(Regex.Replace(trimmed, @"\e\[[0-9;]*m", ""));
                        }
                        return sb.ToString();
                    }
                }
                catch { /* try next command */ }
            }
            return "Could not retrieve app list";
        });

        public async Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => await RunConnected(tvIpAddress, $"launch {tvIpAddress} \"{appId}\"", async device =>
        {
            // Sent here rather than through the engine's LaunchAppAsync, which discards the reply: the
            // TV refuses a launch in text, with no failing status, the way it refuses an install. A
            // flat "App launched." therefore read as success on every attempt — tolerable while this
            // only opened the user's own sideloaded app, wrong now that the toolbox aims it at
            // platform apps the set may well refuse (#641). The raw reply travels back in either
            // case, so the caller can show the user what the TV actually said.
            var reply = (await device.ShellCommandAsync($"0 was_execute {appId}")).Trim();

            switch (TizenLaunchReply.Parse(reply))
            {
                case TizenLaunchVerdict.NotASmartHubApp:
                case TizenLaunchVerdict.Refused:
                    throw new Exception($"The TV would not open {appId}: {reply}");

                case TizenLaunchVerdict.Unknown when reply.Length == 0:
                    // Silence is not a launch. The launcher names every outcome it knows about, so an
                    // empty reply means the verb never reached it (a dropped connection, sdbd cutting
                    // the shell short) — and a claimed success here is what hid every refusal before.
                    throw new Exception($"The TV gave no answer to the launch of {appId}.");

                default:
                    return reply;
            }
        });

        public async Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
        {
            // Resign is a local file operation — no device connection. The certificate password is
            // deliberately left out of the log line.
            var command = $"resign \"{packagePath}\"";
            ProcessResult result;
            try
            {
                var output = await TizenWgtSigner.ReSignWgtWithCertsInPlace(packagePath, authorP12, distributorP12, certPass, backupPath: null);
                result = Ok($"Re-signed in place: {output}");
            }
            catch (Exception ex)
            {
                result = Fail(ex);
            }

            Log(command, result);
            return result;
        }

        public async Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath) =>
            await RunCapturingDeviceLog(tvIpAddress, $"install {tvIpAddress} \"{packagePath}\" {sdkToolPath}", device =>
            {
                var installer = new TizenInstaller(packagePath, device, sdkToolPath);
                return installer.InstallApp();
            });

        public async Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId) => await RunConnected(tvIpAddress, $"uninstall {tvIpAddress} {packageId}", async device =>
        {
            try
            {
                var result = await device.ShellCommandAsync($"0 vd_appuninstall {packageId}");
                if (result.Contains("fail", StringComparison.OrdinalIgnoreCase) || result.Contains("error", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Uninstallation failed");
                return result;
            }
            catch
            {
                // Fallback to pkgcmd, matching the CLI.
                var pkgName = packageId.Split('.')[0];
                return await device.ShellCommandAsync($"pkgcmd -u -n {pkgName} -q");
            }
        });

        public async Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) =>
            await RunCapturingDeviceLog(tvIpAddress, $"permit-install {tvIpAddress} \"{deviceXml}\" {sdkToolPath}", device =>
            {
                var installer = new TizenInstaller(deviceXml, device, sdkToolPath);
                return installer.PermitInstallApp();
            });

        public async Task<ProcessResult> ShellAsync(string tvIpAddress, string command) => await RunConnected(tvIpAddress, $"shell {tvIpAddress} {command}", async device =>
        {
            return await device.ShellCommandAsync(command);
        });

        /// <summary>
        /// Opens a local→TV TCP tunnel (used to attach a debugger to a running app). Unlike every other
        /// call this gets its OWN connection instead of the pooled one: the tunnel outlives the call that
        /// created it, and callers routinely <see cref="DisconnectAsync"/> the TV right afterwards —
        /// which would tear the tunnel down with the pooled connection. Disposing the returned session
        /// closes the tunnel and its connection.
        /// </summary>
        public async Task<IAsyncDisposable> ForwardAsync(string tvIpAddress, int localPort, int remotePort)
        {
            Trace.WriteLine($"[sdb] forward {tvIpAddress} tcp:{localPort} tcp:{remotePort}");
            var device = await ConnectWithRetryAsync(tvIpAddress);
            try
            {
                var session = await device.ForwardAsync(localPort, remotePort);
                return new ForwardSession(session, device);
            }
            catch
            {
                await device.DisposeAsync();
                throw;
            }
        }

        // Runs <paramref name="body"/> on the connection reused for <paramref name="ip"/>, letting it
        // build its own result. Connecting or throwing → ExitCode 1 with the message; a non-zero result
        // (however the body decided that) drops the connection too, so the next call reconnects. There
        // is no body-level retry here, so a failed install is never silently re-run — callers keep
        // their own recovery logic.
        private async Task<ProcessResult> WithDevice(
            string ip, string command, Func<SdbTcpDevice, Task<ProcessResult>> body, bool logOutput = true)
        {
            var result = await RunOnSlot(ip, body);
            Log(command, result, logOutput);
            return result;
        }

        private async Task<ProcessResult> RunOnSlot(string ip, Func<SdbTcpDevice, Task<ProcessResult>> body)
        {
            var slot = _slots.GetOrAdd(ip, _ => new DeviceSlot());

            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                SdbTcpDevice device;
                try
                {
                    device = await slot.GetOrConnectAsync(ip);
                }
                catch (Exception ex)
                {
                    await slot.DropAsync();
                    return Fail(ex);
                }

                ProcessResult result;
                try
                {
                    result = await body(device);
                }
                catch (Exception ex)
                {
                    await slot.DropAsync();
                    return Fail(ex);
                }

                if (result.ExitCode != 0)
                    await slot.DropAsync();

                return result;
            }
            finally
            {
                slot.Gate.Release();
            }
        }

        // Success → ExitCode 0 with the body's text.
        private Task<ProcessResult> RunConnected(string ip, string command, Func<SdbTcpDevice, Task<string>> body) =>
            WithDevice(ip, command, async device => Ok(await body(device)));

        /// <summary>
        /// For install / permit-install: <c>TizenSdb.Core</c> streams the TV's own install log
        /// (<c>installing[100]</c>, <c>install failed[118012]</c>, …) to the console rather than
        /// returning it, and that text is precisely what <see cref="TizenInstallDiagnostics"/>
        /// classifies — the TV reports a failed install with a zero exit status, so the log is the only
        /// signal. The exe used to hand it over as captured process output; capture it here so both
        /// heads keep classifying failures instead of reading a bare "install completed".
        /// The capture tees rather than swallows: the host console stays wired up, so
        /// <see cref="Diagnostics.FileLog"/> keeps mirroring the same lines into the session log the
        /// way it did when this output came from the exe. The redirect is process-wide, so one capture
        /// runs at a time.
        /// </summary>
        private Task<ProcessResult> RunCapturingDeviceLog(string ip, string command, Func<SdbTcpDevice, Task> body) =>
            // logOutput: false — the TV's log already reached the session log through the console tee.
            WithDevice(ip, command, async device =>
            {
                await ConsoleCaptureGate.WaitAsync().ConfigureAwait(false);
                var log = new StringWriter();
                var previous = Console.Out;
                Console.SetOut(new TeeTextWriter(previous, log));
                try
                {
                    await body(device);
                    return Ok(log.ToString());
                }
                catch (Exception ex)
                {
                    // Keep the partial log: it carries the TV's error codes, which is what the failure
                    // is classified on. The exception message is appended, not substituted.
                    return new ProcessResult
                    {
                        ExitCode = 1,
                        Output = $"{log}{ex.Message}",
                        Error = ex.Message,
                    };
                }
                finally
                {
                    Console.SetOut(previous);
                    ConsoleCaptureGate.Release();
                }
            }, logOutput: false);

        // As RunConnected, but retries a transient transport failure on a fresh connection (#524).
        // Only for idempotent reads/queries — see TransientTransportErrors.
        private async Task<ProcessResult> RunWithRetry(
            string ip, string command, Func<SdbTcpDevice, Task<string>> body, int attempts = 3)
        {
            var result = await RunConnected(ip, command, body);
            for (int i = 1; i < attempts && IsTransientTransportError(result); i++)
            {
                await Task.Delay(400 * i).ConfigureAwait(false); // brief backoff before retrying the reset
                result = await RunConnected(ip, command, body);
            }
            return result;
        }

        private static bool IsTransientTransportError(ProcessResult result)
        {
            if (result.ExitCode == 0)
                return false;

            var text = $"{result.Error} {result.Output}";
            return !string.IsNullOrWhiteSpace(text) &&
                TransientTransportErrors.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        // Samsung sdbd sometimes closes a freshly-opened connection mid-handshake
        // ("Remote closed stream while reading"), typically right after a previous connection was
        // torn down. The command never runs — it dies in the CNXN/AUTH handshake — so retry the
        // connect a few times on a fresh socket before giving up. A genuinely offline TV still fails
        // fast (3 quick attempts).
        private static async Task<SdbTcpDevice> ConnectWithRetryAsync(string ip)
        {
            var address = IPAddress.Parse(ip);
            Exception? last = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var device = new SdbTcpDevice(address);
                try
                {
                    await device.ConnectAsync();
                    return device;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await device.DisposeAsync();
                    if (attempt < 3)
                        await Task.Delay(400);
                }
            }

            throw last ?? new InvalidOperationException($"Could not connect to {ip}.");
        }

        /// <summary>
        /// Writes the command and its result to the session log. The exe engine wrote one
        /// <c>Logs/process_&lt;command&gt;_&lt;timestamp&gt;.log</c> file per invocation, holding the merged
        /// stdout/stderr — the record users attach to bug reports. Driving the engine in-process
        /// produces no such files, so the same information is traced into the one
        /// <c>debug_&lt;timestamp&gt;.log</c> instead: same content, in call order, retries included.
        /// </summary>
        private static void Log(string command, ProcessResult result, bool logOutput = true)
        {
            Trace.WriteLine($"[sdb] {command} → exit {result.ExitCode}");

            if (!logOutput)
                return;

            var body = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
            if (!string.IsNullOrWhiteSpace(body))
                Trace.WriteLine(body.TrimEnd());
        }

        private static ProcessResult Ok(string output) => new() { ExitCode = 0, Output = output, Error = string.Empty };

        // The message goes in Output as well as Error: the exe engine merged the process' stderr into
        // Output, so that is where callers both classify failures ("Remote closed channel", transport
        // resets) and read the text they show the user (e.g. "Package resigning failed: {Output}").
        private static ProcessResult Fail(Exception ex) => new() { ExitCode = 1, Output = ex.Message, Error = ex.Message };

        public async ValueTask DisposeAsync()
        {
            foreach (var slot in _slots.Values)
            {
                await slot.Gate.WaitAsync().ConfigureAwait(false);
                try { await slot.DropAsync(); }
                finally { slot.Gate.Release(); }
            }
            _slots.Clear();
        }

        /// <summary>One TV's reused connection plus the gate that serializes calls to it.</summary>
        private sealed class DeviceSlot
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            private SdbTcpDevice? _device;
            private DateTime _lastUsedUtc;

            public async Task<SdbTcpDevice> GetOrConnectAsync(string ip)
            {
                if (_device is not null && DateTime.UtcNow - _lastUsedUtc > IdleTimeout)
                    await DropAsync();

                if (_device is null)
                    _device = await ConnectWithRetryAsync(ip);

                _lastUsedUtc = DateTime.UtcNow;
                return _device;
            }

            public async Task DropAsync()
            {
                var device = _device;
                _device = null;
                if (device is not null)
                {
                    try { await device.DisposeAsync(); } catch { /* already torn down */ }
                }
            }
        }

        /// <summary>Writes to the original console AND a capture buffer (see RunCapturingDeviceLog).</summary>
        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _console;
            private readonly TextWriter _capture;

            public TeeTextWriter(TextWriter console, TextWriter capture)
            {
                _console = console;
                _capture = capture;
            }

            public override Encoding Encoding => _console.Encoding;

            public override void Write(char value)
            {
                _console.Write(value);
                _capture.Write(value);
            }

            public override void Write(string? value)
            {
                _console.Write(value);
                _capture.Write(value);
            }

            public override void WriteLine(string? value)
            {
                _console.WriteLine(value);
                _capture.WriteLine(value);
            }
        }

        /// <summary>Ties a forward tunnel to the dedicated connection it runs on.</summary>
        private sealed class ForwardSession : IAsyncDisposable
        {
            private readonly IAsyncDisposable _session;
            private readonly SdbTcpDevice _device;

            public ForwardSession(IAsyncDisposable session, SdbTcpDevice device)
            {
                _session = session;
                _device = device;
            }

            public async ValueTask DisposeAsync()
            {
                try { await _session.DisposeAsync(); } catch { /* tunnel already down */ }
                try { await _device.DisposeAsync(); } catch { /* already torn down */ }
            }
        }
    }
}
