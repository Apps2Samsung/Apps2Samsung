using System.Linq;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Ties Samsung sign-in to certificate provisioning on the mobile head: reads the target TV's DUID
/// via the in-process engine, then drives the shared <see cref="ITizenCertificateService"/> to mint
/// the author + distributor PKCS#12s. Materializes the bundled Samsung CA files to a real filesystem
/// path first (the cert service reads them via <c>File.*</c>, and Android package assets aren't files).
/// </summary>
public sealed class CertificateProvisioner
{
	// Samsung CA certs shipped as MauiAssets under Resources/Raw/ca and required by the cert service.
	private static readonly string[] CaFiles = { "vd_tizen_dev_author_ca.cer", "vd_tizen_dev_public2.crt", "vd_tizen_dev_partner2.crt" };

	private readonly ISdbEngine _sdb;
	private readonly ITizenCertificateService _certService;

	public CertificateProvisioner(ISdbEngine sdb, ITizenCertificateService certService)
	{
		_sdb = sdb;
		_certService = certService;
	}

	public sealed record Result(string AuthorP12, string DistributorP12, string Password, string ProfileDir, string Duid);

	public async Task<Result> ProvisionAsync(string tvIp, SamsungAuth auth, bool requirePartner = false, Action<string>? progress = null)
	{
		progress?.Invoke("Reading TV DUID…");
		var duidResult = await _sdb.DuidAsync(tvIp);
		var duid = duidResult.Output.Trim();
		// Reject a malformed DUID (e.g. an SDB transport-error string) so it never lands in the cert SAN.
		if (duidResult.ExitCode != 0 || !TizenDuid.IsValid(duid))
			throw new InvalidOperationException(
				$"Could not read a valid TV DUID{(string.IsNullOrWhiteSpace(duidResult.Error) ? "." : $": {duidResult.Error}")}");

		var caPath = await MaterializeCaAsync();
		var profileDir = Path.Combine(FileSystem.AppDataDirectory, "TizenProfile");

		// The target TV's DUID, plus any extra DUIDs the user pre-authorized in settings, so one
		// certificate can cover several TVs.
		var duids = new[] { duid }
			.Concat(MobileSettings.ParseDuids())
			.Where(TizenDuid.IsValid)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		// Opt-in Partner signing (experimental) — needed only by apps that use restricted
		// privileges (e.g. vpnservice). Partner if the global toggle is on OR the selected package
		// declares it needs it; default stays Public.
		var level = (MobileSettings.PartnerSigning || requirePartner)
			? CertificatePrivilegeLevel.Partner
			: CertificatePrivilegeLevel.Public;

		ProgressCallback? cb = progress is null ? null : new ProgressCallback(progress);
		var (authorP12, distributorP12, password) = await _certService.GenerateProfileAsync(
			duids: duids,
			accessToken: auth.access_token,
			userId: auth.userId,
			userEmail: auth.inputEmailID,
			outputPath: profileDir,
			caPath: caPath,
			level: level,
			progress: cb);

		return new Result(authorP12, distributorP12, password, profileDir, duid);
	}

	// Copies the bundled CA certs to <AppData>/ca once, returning that directory.
	private static async Task<string> MaterializeCaAsync()
	{
		var caPath = Path.Combine(FileSystem.AppDataDirectory, "ca");
		Directory.CreateDirectory(caPath);

		foreach (var file in CaFiles)
		{
			var dest = Path.Combine(caPath, file);
			if (File.Exists(dest))
				continue;

			using var src = await FileSystem.OpenAppPackageFileAsync($"ca/{file}");
			using var dst = File.Create(dest);
			await src.CopyToAsync(dst);
		}

		return caPath;
	}
}
