using System.Net.Http.Headers;
using Apps2Samsung.Configuration;
using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Downloads a .wgt and installs it on the TV via the in-process <see cref="ISdbEngine"/>, using
/// the certificates from <see cref="CertificateProvisioner"/>. The install sequence (read capability →
/// (older TVs only) push the device profile → patch → re-sign → install, with the error-code recovery)
/// is Core's <see cref="PackageInstaller"/>, shared with the CLI.
/// </summary>
public sealed class WgtInstaller
{
	private readonly HttpClient _http;
	private readonly PackageInstaller _installer;

	public WgtInstaller(ISdbEngine sdb, HttpClient http, IAppConfig config, IEnumerable<IPackagePatcher> patchers)
	{
		_http = http;
		_installer = new PackageInstaller(sdb, config, patchers);
	}

	public async Task<string> DownloadAsync(string url, Action<string>? progress = null, CancellationToken ct = default)
	{
		progress?.Invoke("Downloading package…");

		var name = url.Split('/').LastOrDefault()?.Split('?')[0];
		if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
			name = "package.wgt";
		var dest = Path.Combine(FileSystem.CacheDirectory, name);

		// Run the transfer off the UI thread with ConfigureAwait(false): on Android, disposing the HTTP
		// response stream can do socket I/O, which StrictMode kills with NetworkOnMainThreadException if
		// the continuation resumes on the main thread.
		try
		{
			await Task.Run(async () =>
			{
				using var req = new HttpRequestMessage(HttpMethod.Get, url);
				req.Headers.UserAgent.ParseAdd("Apps2Samsung-Mobile");
				var token = MobileSettings.GitHubToken;
				if (!string.IsNullOrWhiteSpace(token) && new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
					req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

				using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
				resp.EnsureSuccessStatusCode();
				await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
				await using var dst = File.Create(dest);
				await src.CopyToAsync(dst, ct).ConfigureAwait(false);
			}, ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// A cancelled transfer leaves a partial file nobody will install.
			try { File.Delete(dest); } catch { /* best-effort */ }
			throw;
		}

		return dest;
	}

	/// <param name="step">Told which numbered step the install reached (the progress sheet), with the
	/// TV's install percentage while it reports one.</param>
	/// <param name="ct">Honoured up to the moment the TV is touched for real (old copy removed, package
	/// pushed); after that the install runs to the end, so it never leaves the TV halfway.</param>
	/// <param name="confirmRemove">Asked before the failure recovery removes the installed copy, which
	/// erases that app's settings, sign-in and local data on the TV (#825). False stops the install
	/// with <see cref="RemovalDeclinedException"/>; null (no UI) keeps the remove-and-retry as it was.</param>
	public Task<string> InstallAsync(string tvIp, string wgtPath, CertificateProvisioner.Result cert, Action<string>? progress = null,
		Action<InstallStep, int?>? step = null, CancellationToken ct = default, Func<Task<bool>>? confirmRemove = null)
	{
		// The sequence itself lives in Core (PackageInstaller, shared with the CLI, #836); this
		// head only maps its stages onto the progress sheet's numbered steps.
		Action<InstallStage, int?>? stage = step is null ? null : (s, percent) => step(s switch
		{
			InstallStage.Install => InstallStep.Install,
			InstallStage.Open => InstallStep.Open,
			_ => InstallStep.Sign,
		}, percent);
		return _installer.InstallAsync(tvIp, wgtPath,
			new SigningProfile(cert.AuthorP12, cert.DistributorP12, cert.Password, cert.ProfileDir),
			progress, stage, ct, confirmRemove);
	}
}
