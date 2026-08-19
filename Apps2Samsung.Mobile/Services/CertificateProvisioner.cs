using Apps2Samsung.Certificate;
using Apps2Samsung.Configuration;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Sdb;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Mobile certificate provisioning. Reads the target TV's DUID via the in-process engine, materializes
/// the bundled Samsung CA files to a real path, then drives the shared
/// <see cref="CertificateProvisioningService"/> — which <b>reuses</b> a valid signing profile that
/// already covers the TV and only triggers a Samsung login when it must (re)generate. (Previously the
/// mobile head minted a fresh cert — and forced a login — on every install.)
/// </summary>
public sealed class CertificateProvisioner
{
	// Samsung CA certs shipped as MauiAssets under Resources/Raw/ca and required by the cert service.
	private static readonly string[] CaFiles = { "vd_tizen_dev_author_ca.cer", "vd_tizen_dev_public2.crt", "vd_tizen_dev_partner2.crt" };

	private readonly ISdbEngine _sdb;
	private readonly CertificateProvisioningService _provisioning;
	private readonly IAppConfig _config;

	public CertificateProvisioner(ISdbEngine sdb, CertificateProvisioningService provisioning, IAppConfig config)
	{
		_sdb = sdb;
		_provisioning = provisioning;
		_config = config;
	}

	public sealed record Result(string AuthorP12, string DistributorP12, string Password, string ProfileDir, string Duid);

	public async Task<Result> ProvisionAsync(string tvIp, bool requirePartner = false, Action<string>? progress = null)
	{
		progress?.Invoke("Reading TV DUID…");
		// Retry the read: old/slow TVs often hand back an empty reply or drop the connection mid-command
		// on the first try, then answer on a retry. A validated DUID is returned; anything else throws.
		var duid = await TizenDuidReader.ReadAsync(_sdb, tvIp, progress: progress);

		var caPath = await MaterializeCaAsync();

		// Which certificate/level to sign with, from the user's Certificate preference (Settings):
		//  • Partner   → always Partner.
		//  • Public    → always Public (the user's explicit choice).
		//  • Automatic → Public, unless the selected package declares it needs Partner (requirePartner).
		// Partner is only needed by apps that use restricted privileges (e.g. vpnservice).
		var level = MobileSettings.CertificatePreference switch
		{
			MobileSettings.CertificatePreferencePartner => CertificatePrivilegeLevel.Partner,
			MobileSettings.CertificatePreferencePublic => CertificatePrivilegeLevel.Public,
			_ => requirePartner ? CertificatePrivilegeLevel.Partner : CertificatePrivilegeLevel.Public,
		};

		ProgressCallback? cb = progress is null ? null : new ProgressCallback(progress);
		var profile = await _provisioning.EnsureAsync(
			deviceDuid: duid,
			level: level,
			storePath: _config.CertificateStorePath,
			caPath: caPath,
			manualDuids: MobileSettings.ParseDuids(),
			forceLogin: _config.ForceSamsungLogin,
			onLoginStarting: () => progress?.Invoke("Signing in to Samsung…"),
			progress: cb);

		return new Result(profile.AuthorP12, profile.DistributorP12, profile.Password, profile.ProfileDir, duid);
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
