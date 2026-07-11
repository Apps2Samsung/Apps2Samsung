using System.Linq;
using Apps2Samsung.Interfaces;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Downloads a .wgt and installs it on the TV via the in-process <see cref="ISdbEngine"/>, using
/// the certificates from <see cref="CertificateProvisioner"/>. Mirrors the desktop install sequence:
/// read capability (sdk tool path + platform version) → (older TVs only) push the device profile →
/// re-sign the package with the author/distributor certs → install.
/// </summary>
public sealed class WgtInstaller
{
	// Fallback install staging path when the TV's capability report doesn't include one.
	private const string DefaultSdkToolPath = "/opt/usr/apps/tmp";
	// Below this Tizen version the device profile must be pushed first (permit-install).
	private static readonly Version PushInstallMax = new(4, 0);
	private static readonly Version IntermediateVersion = new(3, 0);
	private const string HomeDeveloperPath = "/home/developer";

	private readonly ISdbEngine _sdb;
	private readonly HttpClient _http;

	public WgtInstaller(ISdbEngine sdb, HttpClient http)
	{
		_sdb = sdb;
		_http = http;
	}

	public async Task<string> DownloadAsync(string url, Action<string>? progress = null)
	{
		progress?.Invoke("Downloading package…");

		var name = url.Split('/').LastOrDefault()?.Split('?')[0];
		if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
			name = "package.wgt";
		var dest = Path.Combine(FileSystem.CacheDirectory, name);

		using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
		resp.EnsureSuccessStatusCode();
		await using (var src = await resp.Content.ReadAsStreamAsync())
		await using (var dst = File.Create(dest))
			await src.CopyToAsync(dst);

		return dest;
	}

	public async Task<string> InstallAsync(string tvIp, string wgtPath, CertificateProvisioner.Result cert, Action<string>? progress = null)
	{
		progress?.Invoke("Reading TV capabilities…");
		var cap = await _sdb.CapabilityAsync(tvIp);
		var sdkToolPath = ParseCapability(cap.Output, "sdk_toolpath") ?? DefaultSdkToolPath;
		Version.TryParse(ParseCapability(cap.Output, "platform_version"), out var version);

		// Older TVs (<= 4.0) need the distributor device profile pushed before install; newer TVs
		// carry the authorization in the re-signed package itself.
		if (version is not null && version <= PushInstallMax)
		{
			progress?.Invoke("Authorizing device…");
			var profileXml = Path.Combine(cert.ProfileDir, "device-profile.xml");
			var target = version < IntermediateVersion ? HomeDeveloperPath : sdkToolPath;
			await _sdb.PermitInstallAsync(tvIp, profileXml, target);
		}

		progress?.Invoke("Re-signing package…");
		var resign = await _sdb.ResignAsync(wgtPath, cert.AuthorP12, cert.DistributorP12, cert.Password);
		if (resign.ExitCode != 0 || resign.Output.Contains("Re-sign failed", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Re-sign failed: {Detail(resign.Error, resign.Output)}");

		progress?.Invoke("Installing on TV…");
		var install = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);
		if (install.ExitCode != 0)
			throw new InvalidOperationException($"Install failed: {Detail(install.Error, install.Output)}");

		return install.Output;
	}

	// Pulls a "  key: value" line out of the capability report.
	private static string? ParseCapability(string output, string key)
	{
		foreach (var line in output.Split('\n'))
		{
			var marker = key + ":";
			var i = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
			if (i >= 0)
				return line[(i + marker.Length)..].Trim();
		}
		return null;
	}

	private static string Detail(string error, string output) =>
		!string.IsNullOrWhiteSpace(error) ? error : output.Trim();
}
