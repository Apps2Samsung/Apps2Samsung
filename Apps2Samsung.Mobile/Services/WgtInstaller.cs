using System.Net.Http.Headers;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Helpers.Core;
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

		// Run the transfer off the UI thread with ConfigureAwait(false): on Android, disposing the HTTP
		// response stream can do socket I/O, which StrictMode kills with NetworkOnMainThreadException if
		// the continuation resumes on the main thread.
		await Task.Run(async () =>
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.UserAgent.ParseAdd("Apps2Samsung-Mobile");
			var token = MobileSettings.GitHubToken;
			if (!string.IsNullOrWhiteSpace(token) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
				req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

			using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
			resp.EnsureSuccessStatusCode();
			await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			await using var dst = File.Create(dest);
			await src.CopyToAsync(dst).ConfigureAwait(false);
		}).ConfigureAwait(false);

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
		var (appId, packageId) = await WgtManifest.ReadIdsAsync(wgtPath);

		// Pre-install Tizen version gate (shared Core check, same as the desktop head): if the package
		// declares a required_version newer than this TV, fail up front with a clear message instead of
		// signing + pushing and getting back the ambiguous [118, -4] "operation not allowed".
		var requiredVersion = await WgtManifest.ReadRequiredVersionAsync(wgtPath);
		if (WgtManifest.RequiresNewerTizen(version, requiredVersion))
			throw new InvalidOperationException(
				$"This app needs Tizen {requiredVersion} or newer, but this TV is Tizen {caps.PlatformVersion}. " +
				"Install an older build of the app, or update the TV's firmware.");

		// Was this package already on the TV before we started? Needed so the failure-cleanup below
		// only clears a partial from a FRESH install and never removes a pre-existing working app.
		bool wasInstalledBefore = false;
		if (!string.IsNullOrWhiteSpace(packageId))
		{
			try
			{
				var listed = await _sdb.AppsAsync(tvIp);
				wasInstalledBefore = listed?.Output?.Contains(packageId!, StringComparison.OrdinalIgnoreCase) ?? false;
			}
			catch { /* best-effort; if we can't tell, treat as fresh (cleanup guard still needs packageId) */ }
		}

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

		// Apply per-app modifications before signing: the user's TVApp channels (m3u8 URLs) into a
		// TVApp package's js/main.js, then the registered patchers — e.g. the custom launcher icon,
		// shared with the desktop head via Core IPackagePatcher. All of it edits one workspace, so
		// the package is unpacked once and rezipped once however much applies to it.
		var channels = TvAppChannelInjector.AppliesTo(wgtPath)
			? MobileSettings.GetTvAppChannels()
			: (IReadOnlyList<TvChannel>)Array.Empty<TvChannel>();
		var patchers = _patchers.Where(p => p.CanHandle(wgtPath)).ToList();

		if (channels.Count > 0 || patchers.Count > 0)
		{
			progress?.Invoke(channels.Count > 0 ? "Applying TVApp channels…" : "Applying customizations…");

			using var workspace = PackageWorkspace.Extract(wgtPath);

			if (channels.Count > 0)
				await TvAppChannelInjector.InjectChannelsAsync(workspace, channels);

			foreach (var patcher in patchers)
				await patcher.ApplyAsync(workspace);

			// A patcher that matched on the file name but found nothing to do leaves the package
			// exactly as downloaded rather than as a recompressed copy of itself.
			workspace.RepackIfChanged();
		}

		// The certificate must already be inside its validity window: Tizen checks the signature
		// against the TV's clock and refuses one whose start date is still in the future
		// ("Certificate in signature is not valid yet"). Only waiting fixes that, so stop before we
		// sign and push — InstallerPage turns this into a popup naming the moment it becomes usable.
		var validity = CertificateValidity.CheckSigningProfile(cert.AuthorP12, cert.DistributorP12, cert.Password);
		if (validity.IsNotYetValid)
			throw new CertificateNotYetValidException(validity);

		progress?.Invoke("Re-signing package…");
		var resign = await _sdb.ResignAsync(wgtPath, cert.AuthorP12, cert.DistributorP12, cert.Password);
		if (resign.ExitCode != 0 || resign.Output.Contains("Re-sign failed", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Re-sign failed: {Detail(resign.Error, resign.Output)}");

		progress?.Invoke("Installing on TV…");
		var install = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);
		// The Tizen install helper returns a zero exit code (cmd_ret:0) even when the app install
		// itself failed (e.g. "app_id[...] install failed[118]"). Trusting the exit code alone made
		// the app report "✓ Installed" for installs that never landed on the TV — so classify the
		// OUTPUT as well, mirroring the desktop head, and route real failures into recovery.
		if (install.ExitCode != 0 || TizenInstallDiagnostics.IndicatesFailure(install.Output))
			install = await RecoverInstallAsync(tvIp, wgtPath, sdkToolPath, packageId, install, progress, wasInstalledBefore);

		if (MobileSettings.OpenAfterInstall && !string.IsNullOrWhiteSpace(appId))
		{
			progress?.Invoke("Launching on TV…");
			try { await _sdb.LaunchAsync(tvIp, appId!); } catch { /* launch is best-effort */ }
		}

		return install.Output;
	}

	// [118, -22] from the TV's security manager. Worded the same as the desktop head's
	// "packageIdBlocked" string, which is the translated one.
	private const string PackageIdBlockedMessage =
		"The TV refused this package because the package id it uses is blocked on this set ([118, -22]). " +
		"An older copy is still registered under a different signing certificate, or an interrupted " +
		"uninstall left a record behind that the app list no longer shows. Delete the app on the TV itself " +
		"(Apps list, press and hold, Remove) and install again, install a build that ships its own package " +
		"id (a \"secondary\" variant), or reset Smart Hub as a last resort.";

	// Overwrite-install retry is on by default; the "Override existing app" setting (key shared with
	// MobileSettings.TryOverwrite) can turn it off.
	private static bool TryOverwriteEnabled => Preferences.Get("try_overwrite", true);

	// Interprets a non-zero install result and, where it helps, removes the old copy and retries once —
	// mirroring the desktop head's error-code handling. Returns the successful result or throws with an
	// actionable message.
	private async Task<ProcessResult> RecoverInstallAsync(
		string tvIp, string wgtPath, string sdkToolPath, string? packageId, ProcessResult failed, Action<string>? progress,
		bool wasInstalledBefore)
	{
		var output = failed.Output ?? string.Empty;

		// Fresh-install cleanup guard: a failed install of an app that was NOT already on the TV can
		// leave a partial package dir wasting storage. Best-effort clear it before we throw — but ONLY
		// when it was a fresh install (never remove a pre-existing working app on a failed reinstall).
		// Not called on the transport-lost / [118,-4] / cert-mismatch paths (those throw earlier).
		async Task ClearPartialIfFresh()
		{
			if (!wasInstalledBefore && !string.IsNullOrWhiteSpace(packageId))
			{
				try { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* best-effort */ }
			}
		}

		// Environmental — a broken route to the TV. Retrying the same push can't help.
		if (TizenInstallDiagnostics.IsTransportLost(output))
			throw new InvalidOperationException(
				"Connection to the TV was interrupted. Check Wi-Fi (and that the TV is awake), then try again.");

		// [118, -4] "operation not allowed": the TV refused the package. Ambiguous — could be the app
		// targeting a newer Tizen than this TV, OR the package needing Partner signing / a privilege the
		// certificate doesn't grant. Don't assert "TV too old" (it misleads when other apps install fine).
		if (TizenInstallDiagnostics.IsApiVersionMismatch(output))
			throw new InvalidOperationException(
				"The TV refused this package ([118, -4]). Either the app needs a newer Tizen than this TV, or it needs Partner signing / a privilege your certificate doesn't allow. If other apps install fine, turn on Partner signing in Settings and retry, or try an older build.");

		// [116] "download failed": the TV has no room for the package. Uninstalling and pushing the
		// same file again cannot create space, and the raw wascmd dump it used to end in gave the
		// user no clue what was wrong (#666). Stop here with the actual reason.
		if (TizenInstallDiagnostics.IsInsufficientSpace(output))
		{
			await ClearPartialIfFresh();
			throw new InvalidOperationException(
				"Not enough free space on the TV [116]. Remove some apps on the TV (Settings → Support → Device Care → Manage Storage), then install again.");
		}

		bool certMismatch = TizenInstallDiagnostics.IsCertificateMismatch(output);

		// [118, -22]: the TV's security manager refused the package id itself. Removing the old copy is
		// the one remedy we can drive from here, so it goes through the retry below — but if the TV has
		// nothing to remove, say what is actually wrong instead of repeating the raw output (#702).
		bool idBlocked = TizenInstallDiagnostics.IsPackageIdBlocked(output);

		// Recoverable by removing the old copy first: certificate mismatch, a blocked or conflicting
		// package id, or a generic failure. Try exactly one clean reinstall.
		bool recoverable = certMismatch ||
						   idBlocked ||
						   TizenInstallDiagnostics.IsPackageIdConflict(output) ||
						   TizenInstallDiagnostics.IsGenericFailure(output);

		if (recoverable && TryOverwriteEnabled && !string.IsNullOrWhiteSpace(packageId))
		{
			progress?.Invoke("Install failed — removing the old copy and retrying…");
			try { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* best-effort */ }

			var retry = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);
			if (retry.ExitCode == 0 && !TizenInstallDiagnostics.IndicatesFailure(retry.Output))
				return retry;

			// Still failing after a clean slate — surface the most useful message.
			if (TizenInstallDiagnostics.IsCertificateMismatch(retry.Output))
				throw new InvalidOperationException(
					"The TV already has this app signed with a different certificate. Remove it on the TV (Apps → delete), then install again.");

			if (TizenInstallDiagnostics.IsPackageIdBlocked(retry.Output))
				throw new InvalidOperationException(PackageIdBlockedMessage);

			await ClearPartialIfFresh();
			throw new InvalidOperationException($"Install failed: {Detail(retry.Error, retry.Output)}");
		}

		// Not retried (recovery off or no package id) — give the clearest message we can.
		if (idBlocked)
			throw new InvalidOperationException(PackageIdBlockedMessage);

		if (certMismatch)
			throw new InvalidOperationException(
				"The TV already has this app signed with a different certificate. Remove it on the TV (Apps → delete) and install again, or enable \"Override existing app\" in Settings.");

		await ClearPartialIfFresh();
		throw new InvalidOperationException($"Install failed: {Detail(failed.Error, failed.Output)}");
	}

	private static string Detail(string error, string output) =>
		!string.IsNullOrWhiteSpace(error) ? error : output.Trim();
}
