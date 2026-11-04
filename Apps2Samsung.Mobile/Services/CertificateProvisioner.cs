using Apps2Samsung.Certificate;
using Apps2Samsung.Configuration;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
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

	/// <param name="Tizen">Set when the profile is a Tizen certificate (#988): a [118, -12] chain
	/// refusal then means the TV only takes a Samsung one.</param>
	public sealed record Result(string AuthorP12, string DistributorP12, string Password, string ProfileDir, string Duid,
		TizenCertificateProfile? Tizen = null);

	/// <summary>How a Tizen certificate run ended (#988): the profile, or the switch to a Samsung one.</summary>
	public sealed record TizenOutcome(Result? Result, bool Created, bool SamsungInstead);

	/// <summary>
	/// A Tizen certificate (#988): the SDK's, made on the phone with no account and no DUID, or the
	/// one already on it for the level the Settings switch means. No TV is needed; with
	/// <paramref name="tvIp"/> the DUID is read for the package-id rename memory (#839) and a TV that
	/// will not hand it out only loses that.
	/// </summary>
	/// <param name="beforeCreateAsync">Awaited when the certificate has to be made, with the level:
	/// <see cref="CertificateFlow"/> shows the sheet here. False means the user wants a Samsung
	/// certificate instead; the caller switches the setting.</param>
	public async Task<TizenOutcome> ProvisionTizenAsync(
		string? tvIp,
		bool requirePartner = false,
		Action<string>? progress = null,
		Func<TizenDistributorLevel, CancellationToken, Task<bool>>? beforeCreateAsync = null,
		Action<CertificateStage>? stage = null,
		CancellationToken ct = default)
	{
		var level = Apps2Samsung.Certificate.CertificateKind.TizenLevel(MobileSettings.CertificatePreference, requirePartner);
		var store = _config.CertificateStorePath;
		bool needsNew = _config.ForceSamsungLogin || !TizenProfileStore.HasProfile(store, level);

		if (needsNew && beforeCreateAsync is not null && !await beforeCreateAsync(level, ct))
			return new TizenOutcome(null, false, true);

		var duid = string.Empty;
		if (tvIp is not null)
		{
			try
			{
				progress?.Invoke(L10n.Get("statusReadingTvDuid"));
				duid = await TizenDuidReader.ReadAsync(_sdb, tvIp, progress: progress);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Trace.WriteLine($"[cert] DUID not read for the Tizen certificate (not needed): {ex.Message}");
			}
		}

		ProgressCallback cb = key =>
		{
			if (CertificateStages.FromProgressKey(key) is { } next)
				stage?.Invoke(next);
			progress?.Invoke(L10n.Get(key));
		};
		var profile = await TizenProfileStore.EnsureAsync(store, level, force: _config.ForceSamsungLogin, progress: cb);
		return new TizenOutcome(
			new Result(profile.AuthorP12, profile.DistributorP12, profile.Password, profile.ProfileDir, duid, profile),
			needsNew, false);
	}

	/// <param name="beforeLoginAsync">Awaited when a certificate has to be made, before the Samsung
	/// sign-in, with the level and the TV's DUID: <see cref="CertificateFlow"/> shows the certificate
	/// sheet here (the DUID tells an imported certificate's coverage, #921).</param>
	/// <param name="stage">The certificate sheet's step, as the certificate service moves on.</param>
	/// <param name="knownDuid">The TV's DUID when the caller has it already; null reads it over SDB.</param>
	public async Task<Result> ProvisionAsync(
		string tvIp,
		bool requirePartner = false,
		Action<string>? progress = null,
		Func<CertificatePrivilegeLevel, string, CancellationToken, Task>? beforeLoginAsync = null,
		Action<CertificateStage>? stage = null,
		CancellationToken ct = default,
		string? knownDuid = null)
	{
		string duid;
		if (knownDuid is not null)
		{
			// The caller names the TV (a manual DUID from Settings, #979): nothing is read over SDB,
			// so this works while the TV's Host PC IP points elsewhere.
			duid = knownDuid;
		}
		else
		{
			progress?.Invoke(L10n.Get("statusReadingTvDuid"));
			// Retry the read: old/slow TVs often hand back an empty reply or drop the connection mid-command
			// on the first try, then answer on a retry. A validated DUID is returned; anything else throws.
			duid = await TizenDuidReader.ReadAsync(_sdb, tvIp, progress: progress);
		}

		var caPath = await MaterializeCaAsync();

		// The selected package can only be installed Partner-signed (it declares a Partner-only
		// privilege such as vpnservice or drminfo) and there is no Partner certificate yet: switch the
		// Settings preference to Partner so one is actually created, and so the picker shows the level
		// we sign with. Without this the install reaches the TV as Public and is rejected with
		// MISMATCHED_PRIVILEGE_LEVEL. The user can switch back to Automatic/Public afterwards.
		if (Apps2Samsung.Certificate.CertificatePreference.ShouldSwitchToPartner(
				MobileSettings.CertificatePreference, requirePartner,
				CertificateProvisioningService.HasProfile(_config.CertificateStorePath, CertificatePrivilegeLevel.Partner)))
		{
			MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePartner;
			progress?.Invoke(L10n.Get("partnerSigningAutoEnabled"));
		}

		// Which certificate/level to sign with, from the user's Certificate preference (Settings);
		// the rule is shared with the desktop.
		var level = Apps2Samsung.Certificate.CertificatePreference.ResolveLevel(
			MobileSettings.CertificatePreference, requirePartner);

		// The certificate service reports localization keys: localized here, and the sheet's step
		// follows from the key.
		ProgressCallback cb = key =>
		{
			if (CertificateStages.FromProgressKey(key) is { } next)
				stage?.Invoke(next);
			progress?.Invoke(L10n.Get(key));
		};
		var profile = await _provisioning.EnsureAsync(
			deviceDuid: duid,
			level: level,
			storePath: _config.CertificateStorePath,
			caPath: caPath,
			manualDuids: MobileSettings.ParseDuids(),
			forceLogin: _config.ForceSamsungLogin,
			onLoginStarting: () => progress?.Invoke(L10n.Get("SamsungLogin")),
			progress: cb,
			beforeLoginAsync: beforeLoginAsync is null ? null : (level, token) => beforeLoginAsync(level, duid, token),
			ct: ct);

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
