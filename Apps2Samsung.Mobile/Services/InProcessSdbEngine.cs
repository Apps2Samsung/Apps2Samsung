using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using TizenSdb;
using TizenSdb.SdbClient;
using TizenSdb.SigningManager;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Mobile <see cref="ISdbEngine"/>: drives <c>TizenSdb.Core</c> in-process (no exe). Each call
/// opens a fresh <see cref="SdbTcpDevice"/> connection, runs the operation, and disposes — mirroring
/// how the desktop CLI runs one process per command, so the <see cref="ProcessResult.Output"/> it
/// returns matches the CLI's stdout that the shared orchestration/regex parsing expects.
/// </summary>
public sealed class InProcessSdbEngine : ISdbEngine
{
	// Commands ListApps tries in order (first useful reply wins) — ported from the CLI.
	private static readonly string[] AppListCommands =
	{
		"0 vd_applist", "applist", "pkgcmd -l", "pm list packages", "ls /usr/apps", "ls /opt/usr/apps",
	};

	// Commands diagnose probes — ported from the CLI. The "0 vd_appuninstall test" line is what the
	// desktop's diagnose parser keys on, so its exact "Testing '…': SUCCESS/FAILED" format is preserved.
	private static readonly string[] DiagnoseCommands =
	{
		"0 getduid", "host:version", "host:features", "shell:uname -a", "shell:ls /usr/apps",
		"shell:pwd", "shell:whoami", "0 vd_applist", "0 vd_appuninstall test", "pkgcmd -l",
	};

	public async Task<ProcessResult> DevicesAsync(string tvIpAddress) => await RunConnected(tvIpAddress, device =>
	{
		var parts = device.DeviceId.Split("::", StringSplitOptions.RemoveEmptyEntries);
		return Task.FromResult(parts.Length >= 2 ? parts[1] : string.Empty);
	});

	public Task<ProcessResult> DisconnectAsync(string tvIpAddress)
		// The CLI's disconnect is a no-op (each command already tears down its own connection).
		=> Task.FromResult(Ok($"* Disconnected from {tvIpAddress}"));

	public async Task<ProcessResult> CapabilityAsync(string tvIpAddress) => await RunConnected(tvIpAddress, async device =>
	{
		var caps = await device.CapabilityAsync();
		var sb = new StringBuilder();
		foreach (var cap in caps)
			sb.AppendLine($"  {cap.Key}: {cap.Value}");
		return sb.ToString();
	});

	public async Task<ProcessResult> DuidAsync(string tvIpAddress) => await RunConnected(tvIpAddress, async device =>
	{
		var duid = await device.ShellCommandAsync("0 getduid");
		return duid.Trim();
	});

	public async Task<ProcessResult> DiagnoseAsync(string tvIpAddress) => await RunConnected(tvIpAddress, async device =>
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
		return sb.ToString();
	});

	public async Task<ProcessResult> AppsAsync(string tvIpAddress) => await RunConnected(tvIpAddress, async device =>
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

	public async Task<ProcessResult> LaunchAsync(string tvIpAddress, string appId) => await RunConnected(tvIpAddress, async device =>
	{
		await device.LaunchAppAsync(appId);
		return "App launched.";
	});

	public async Task<ProcessResult> ResignAsync(string packagePath, string authorP12, string distributorP12, string certPass)
	{
		// Resign is a local file operation — no device connection.
		try
		{
			var output = await TizenWgtSigner.ReSignWgtWithCertsInPlace(packagePath, authorP12, distributorP12, certPass, backupPath: null);
			return Ok($"Re-signed in place: {output}");
		}
		catch (Exception ex)
		{
			return Fail(ex);
		}
	}

	public async Task<ProcessResult> InstallAsync(string tvIpAddress, string packagePath, string sdkToolPath) => await RunConnected(tvIpAddress, async device =>
	{
		var installer = new TizenInstaller(packagePath, device, sdkToolPath);
		await installer.InstallApp();
		return $"Install completed{(installer.PackageId is { } id ? $": {id}" : string.Empty)}";
	});

	public async Task<ProcessResult> UninstallAsync(string tvIpAddress, string packageId) => await RunConnected(tvIpAddress, async device =>
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

	public async Task<ProcessResult> PermitInstallAsync(string tvIpAddress, string deviceXml, string sdkToolPath) => await RunConnected(tvIpAddress, async device =>
	{
		var installer = new TizenInstaller(deviceXml, device, sdkToolPath);
		await installer.PermitInstallApp();
		return "Push completed successfully";
	});

	// Opens a connection, runs <paramref name="body"/>, always disposes. Maps success to ExitCode 0
	// (with the body's text as Output) and any failure to ExitCode 1 with the message in Error —
	// the same shape the desktop's ExeSdbEngine surfaces from the process result.
	private static async Task<ProcessResult> RunConnected(string ip, Func<SdbTcpDevice, Task<string>> body)
	{
		SdbTcpDevice? device = null;
		try
		{
			device = new SdbTcpDevice(IPAddress.Parse(ip));
			await device.ConnectAsync();
			return Ok(await body(device));
		}
		catch (Exception ex)
		{
			return Fail(ex);
		}
		finally
		{
			if (device is not null)
				await device.DisposeAsync();
		}
	}

	private static ProcessResult Ok(string output) => new() { ExitCode = 0, Output = output, Error = string.Empty };

	private static ProcessResult Fail(Exception ex) => new() { ExitCode = 1, Output = string.Empty, Error = ex.Message };
}
