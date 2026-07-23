using System.IO.Compression;
using System.Linq;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;
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
	private readonly ISdbEngine _sdb;
	private readonly HttpClient _http;
	private readonly IEnumerable<IPackagePatcher> _patchers;

	public WgtInstaller(ISdbEngine sdb, HttpClient http, IEnumerable<IPackagePatcher> patchers)
	{
		_sdb = sdb;
		_http = http;
		_patchers = patchers;
	}

	public async Task<string> DownloadAsync(string url, Action<string>? progress = null)
	{
		progress?.Invoke("Downloading package…");

		var name = url.Split('/').LastOrDefault()?.Split('?')[0];
		if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
			name = "package.wgt";
		var dest = Path.Combine(FileSystem.CacheDirectory, name);

		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.UserAgent.ParseAdd("Apps2Samsung-Mobile");
		var token = MobileSettings.GitHubToken;
		if (!string.IsNullOrWhiteSpace(token) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
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
		var caps = TizenCapabilities.Parse(cap.Output);
		var sdkToolPath = caps.SdkToolPath;
		var version = caps.Version;

		// The Tizen app/package ids live in the package's config.xml; needed to remove an old
		// version before install and/or launch the app afterwards.
		var (appId, packageId) = ReadPackageIds(wgtPath);

		if (MobileSettings.DeletePreviousInstall && !string.IsNullOrWhiteSpace(packageId))
		{
			progress?.Invoke("Removing old version…");
			try { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* nothing to remove */ }
		}

		// Older TVs (<= 4.0) need the distributor device profile pushed before install; newer TVs
		// carry the authorization in the re-signed package itself. Thresholds live in Core so both
		// heads agree.
		if (TizenPermitInstall.IsRequired(version))
		{
			progress?.Invoke("Authorizing device…");
			var profileXml = Path.Combine(cert.ProfileDir, "device-profile.xml");
			await TizenPermitInstall.EnsureAsync(_sdb, tvIp, version, sdkToolPath, profileXml);
		}

		// Apply per-app modifications before signing — e.g. inject the user's TVApp channels
		// (m3u8 URLs) into a TVApp package's js/main.js. Shared logic lives in Core.
		if (TvAppChannelInjector.AppliesTo(wgtPath))
		{
			var channels = MobileSettings.GetTvAppChannels();
			if (channels.Count > 0)
			{
				progress?.Invoke("Applying TVApp channels…");
				await TvAppChannelInjector.InjectChannelsAsync(wgtPath, channels);
			}
		}

		// Apply registered package patchers before signing — e.g. the user's custom launcher icon.
		// Shared with the desktop head via Core IPackagePatcher (composes with the TVApp inject above).
		foreach (var patcher in _patchers.Where(p => p.CanHandle(wgtPath)))
		{
			progress?.Invoke("Applying customizations…");
			await patcher.ApplyAsync(wgtPath);
		}

		progress?.Invoke("Re-signing package…");
		var resign = await _sdb.ResignAsync(wgtPath, cert.AuthorP12, cert.DistributorP12, cert.Password);
		if (resign.ExitCode != 0 || resign.Output.Contains("Re-sign failed", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Re-sign failed: {Detail(resign.Error, resign.Output)}");

		progress?.Invoke("Installing on TV…");
		var install = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);
		if (install.ExitCode != 0)
			throw new InvalidOperationException($"Install failed: {Detail(install.Error, install.Output)}");

		if (MobileSettings.OpenAfterInstall && !string.IsNullOrWhiteSpace(appId))
		{
			progress?.Invoke("Launching on TV…");
			try { await _sdb.LaunchAsync(tvIp, appId!); } catch { /* launch is best-effort */ }
		}

		return install.Output;
	}

	// Reads the Tizen application id and package id from the package's config.xml. A .wgt is a zip;
	// its root config.xml carries <tizen:application id="<pkg>.<app>" package="<pkg>" .../>.
	private static (string? AppId, string? PackageId) ReadPackageIds(string wgtPath)
	{
		try
		{
			using var zip = ZipFile.OpenRead(wgtPath);
			var entry = zip.GetEntry("config.xml");
			if (entry is null)
				return (null, null);

			using var stream = entry.Open();
			var doc = XDocument.Load(stream);
			XNamespace tizen = "http://tizen.org/ns/widgets";
			var app = doc.Descendants(tizen + "application").FirstOrDefault();
			if (app is null)
				return (null, null);

			var appId = app.Attribute("id")?.Value;
			var packageId = app.Attribute("package")?.Value
				?? appId?.Split('.').FirstOrDefault();
			return (appId, packageId);
		}
		catch
		{
			return (null, null);
		}
	}

	private static string Detail(string error, string output) =>
		!string.IsNullOrWhiteSpace(error) ? error : output.Trim();
}
