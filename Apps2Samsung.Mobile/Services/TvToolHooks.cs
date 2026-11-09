using Apps2Samsung.Install;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Localization;
using Apps2Samsung.Sdb;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// What the TV toolbox and the TV-Store tab (#1041) both borrow from the phone's install flow: the
/// signing profile an install would use, and a package picked from the phone. One copy, so the
/// toolbox's Sign a package and the TV-Store tab's Arm and Send a package cannot drift apart.
/// </summary>
public static class TvToolHooks
{
	/// <summary>
	/// The profile an install on <paramref name="tvIp"/> would sign with: the certificate flow, sheet
	/// included, when none covers this TV yet. Arm TV-Store (#844) hands it over; Sign a package (#979)
	/// asks the same, Partner when the package needs it, and falls back to the first manual DUID from
	/// Settings when the TV cannot be read over SDB (a Host PC IP that points elsewhere), as the CLI's
	/// --duids does. Throws <see cref="OperationCanceledException"/> when the sheet is dismissed.
	/// </summary>
	public static Func<bool, Action<string>, Task<SigningProfile?>> SigningProfileFor(
		CertificateFlow certFlow, ISdbEngine sdb, INavigation navigation, string tvIp, string label) => async (requirePartner, report) =>
	{
		// A Tizen certificate (#988) names no TV: no DUID needed, the flow reads it only for the
		// rename memory and goes on without it. Unless its sheet switches to Samsung.
		if (MobileSettings.UsesTizenCertificate)
		{
			var (tizenCert, _) = await certFlow.RunAsync(navigation, tvIp, label, requirePartner, report, new CancellationTokenSource());
			return new SigningProfile(tizenCert.AuthorP12, tizenCert.DistributorP12, tizenCert.Password, tizenCert.ProfileDir);
		}
		string? duid;
		try
		{
			report(L10n.Get("statusReadingTvDuid"));
			duid = await TizenDuidReader.ReadAsync(sdb, tvIp, progress: report);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Trace.WriteLine($"[toolbox] DUID over SDB failed, trying the manual DUIDs: {ex.Message}");
			duid = MobileSettings.ParseDuids().FirstOrDefault();
			if (duid is null)
			{
				report(L10n.Get("lblToolboxPackageNoDuid"));
				return null;
			}
		}
		var (cert, _) = await certFlow.RunAsync(navigation, tvIp, label, requirePartner, report, new CancellationTokenSource(), duid);
		return new SigningProfile(cert.AuthorP12, cert.DistributorP12, cert.Password, cert.ProfileDir);
	};

	/// <summary>
	/// Lets the user pick a local .wgt or .tpk and copies it into the cache, so re-signing, the
	/// customization and the clean-up never touch their own file. Returns the copy's path, or null
	/// when nothing valid was picked; <paramref name="report"/> gets the reason in words.
	/// </summary>
	public static async Task<string?> PickPackageCopyAsync(Action<string> report)
	{
		try
		{
			var picked = await LocalFilePicker.PickAsync();
			if (picked is null)
			{
				report(L10n.Get("statusNoFileSelected"));
				return null;
			}
			if (!picked.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase) &&
				!picked.FileName.EndsWith(".tpk", StringComparison.OrdinalIgnoreCase))
			{
				report(L10n.Get("statusChooseWgt"));
				return null;
			}

			var dest = Path.Combine(FileSystem.CacheDirectory, picked.FileName);
			File.Copy(picked.LocalPath, dest, overwrite: true);
			report(string.Format(L10n.Get("statusCustomPackageReady"), picked.FileName));
			return dest;
		}
		catch (Exception ex)
		{
			report(string.Format(L10n.Get("statusFileUnreadable"), ex.Message));
			return null;
		}
	}
}
