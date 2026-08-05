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
/// Mobile <see cref="ISdbEngine"/>: drives <c>TizenSdb.Core</c> in-process (no exe). One
/// <see cref="SdbTcpDevice"/> connection is reused across sequential calls to the same TV rather
/// than reconnecting per command — the reconnect churn is what triggered Samsung sdbd to close a
/// fresh connection mid-handshake. Engine calls in the mobile flow are sequential (the network scan
/// uses raw TCP, not this engine), so a single gate serializes access; the connection is dropped on
/// any failure or when the target IP changes, so the next call reconnects.
/// </summary>
public sealed class InProcessSdbEngine : ISdbEngine, IAsyncDisposable
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private SdbTcpDevice? _device;
	private string? _deviceIp;

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

	public async Task<ProcessResult> DisconnectAsync(string tvIpAddress)
	{
		// Drop the reused connection so the next call reconnects.
		await _gate.WaitAsync().ConfigureAwait(false);
		try { await DropAsync(); }
		finally { _gate.Release(); }
		return Ok($"* Disconnected from {tvIpAddress}");
	}

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

	// Runs <paramref name="body"/> on a reused connection to <paramref name="ip"/>. Success →
	// ExitCode 0 with the body's text; any failure → ExitCode 1 with the message in Error (same shape
	// the desktop's ExeSdbEngine surfaces). On any failure the connection is dropped so the next call
	// reconnects. Error semantics are unchanged — there is no body-level retry, so a failed install is
	// never silently re-run (callers keep their own recovery logic).
	private async Task<ProcessResult> RunConnected(string ip, Func<SdbTcpDevice, Task<string>> body)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			SdbTcpDevice device;
			try
			{
				device = await GetOrConnectAsync(ip);
			}
			catch (Exception ex)
			{
				await DropAsync();
				return Fail(ex);
			}

			try
			{
				return Ok(await body(device));
			}
			catch (Exception ex)
			{
				await DropAsync();
				return Fail(ex);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// Returns the cached connection for this IP, or connects (with handshake retry) and caches it.
	// Switching to a different IP drops the old connection first.
	private async Task<SdbTcpDevice> GetOrConnectAsync(string ip)
	{
		if (_device is not null && _deviceIp == ip)
			return _device;

		await DropAsync();
		_device = await ConnectWithRetryAsync(ip);
		_deviceIp = ip;
		return _device;
	}

	private async Task DropAsync()
	{
		var device = _device;
		_device = null;
		_deviceIp = null;
		if (device is not null)
		{
			try { await device.DisposeAsync(); } catch { /* already torn down */ }
		}
	}

	// Samsung sdbd sometimes closes a freshly-opened connection mid-handshake
	// ("Remote closed stream while reading"), typically right after a previous connection was
	// torn down (every engine call reconnects). The command never runs — it dies in the CNXN/AUTH
	// handshake — so retry the connect a few times on a fresh socket before giving up. A genuinely
	// offline TV still fails fast (3 quick attempts).
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

	private static ProcessResult Ok(string output) => new() { ExitCode = 0, Output = output, Error = string.Empty };

	private static ProcessResult Fail(Exception ex) => new() { ExitCode = 1, Output = string.Empty, Error = ex.Message };

	public async ValueTask DisposeAsync()
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try { await DropAsync(); }
		finally { _gate.Release(); }
	}
}
