using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Mobile.Pages;

namespace Apps2Samsung.Mobile.Services;

/// <summary>
/// Provisioning with the certificate sheet on top (#755). A certificate that already covers the TV
/// is reused without any UI; only when one has to be made does the sheet appear, wait for Sign in
/// with Samsung, follow the steps and close itself. Shared by the install and by Settings ›
/// Installing › Prepare the certificate now.
/// </summary>
public sealed class CertificateFlow
{
	private readonly CertificateProvisioner _provisioner;

	public CertificateFlow(CertificateProvisioner provisioner)
	{
		_provisioner = provisioner;
	}

	/// <summary>
	/// Provisions for <paramref name="tvIp"/>. Cancel on the sheet cancels <paramref name="cancel"/>
	/// and throws <see cref="OperationCanceledException"/>. <c>Created</c> is true when a certificate
	/// was made (the sheet was shown), false when an existing one was reused. <paramref name="duid"/>
	/// names the TV when the caller has its DUID already (#979); null reads it over SDB.
	/// </summary>
	public async Task<(CertificateProvisioner.Result Result, bool Created)> RunAsync(
		INavigation navigation,
		string tvIp,
		string tvName,
		bool requirePartner,
		Action<string>? report,
		CancellationTokenSource cancel,
		string? duid = null)
	{
		// A Tizen certificate (#988) when Settings says so; its sheet's way out is a Samsung one,
		// the setting is switched by then and the Samsung path below runs as if chosen all along.
		if (MobileSettings.UsesTizenCertificate)
		{
			var tizen = await RunTizenAsync(navigation, tvName, requirePartner, report, cancel, tvIp);
			if (tizen is { } made)
				return made;
		}

		CertificateSheetPage? sheet = null;

		async Task BeforeLoginAsync(CertificatePrivilegeLevel level, string duid, CancellationToken ct)
		{
			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				sheet = new CertificateSheetPage(tvName, level, duid);
				await navigation.PushModalAsync(sheet);
			});
			if (!await sheet!.SignInChosen)
				cancel.Cancel();
			cancel.Token.ThrowIfCancellationRequested();
		}

		try
		{
			var result = await _provisioner.ProvisionAsync(
				tvIp,
				requirePartner,
				message =>
				{
					report?.Invoke(message);
					MainThread.BeginInvokeOnMainThread(() => sheet?.Report(message));
				},
				BeforeLoginAsync,
				stage => MainThread.BeginInvokeOnMainThread(() => sheet?.SetStage(stage)),
				cancel.Token,
				duid);

			if (sheet is { } done)
			{
				await MainThread.InvokeOnMainThreadAsync(async () =>
				{
					done.SetStage(CertificateStage.Done);
					// Long enough to see every step ticked before the sheet goes.
					await Task.Delay(700);
					await done.CloseAsync();
				});
			}
			return (result, sheet is not null);
		}
		catch (Exception ex) when (sheet is { } open)
		{
			// Cancelled (here or in Samsung's sign-in page) or an account the heads explain on a sheet
			// of their own: just close. Anything else stays on the sheet with Close.
			if (ex is OperationCanceledException or SamsungAccountEmailMissingException)
				await MainThread.InvokeOnMainThreadAsync(open.CloseAsync);
			else
				MainThread.BeginInvokeOnMainThread(() => open.Fail(ErrorText.Describe(ex, "certificate")));
			throw;
		}
	}

	/// <summary>
	/// The Tizen certificate (#988): the one on the phone for the level the Settings switch means,
	/// without any UI, or a new one made behind the two-step sheet. Null when the user chose a
	/// Samsung certificate on that sheet: the setting is switched to Samsung by then. Cancel on the
	/// sheet throws <see cref="OperationCanceledException"/>. <paramref name="tvName"/> null means
	/// Settings asked with no TV in mind; <paramref name="tvIp"/> reads the DUID for the rename memory.
	/// </summary>
	public async Task<(CertificateProvisioner.Result Result, bool Created)?> RunTizenAsync(
		INavigation navigation,
		string? tvName,
		bool requirePartner,
		Action<string>? report,
		CancellationTokenSource cancel,
		string? tvIp = null)
	{
		CertificateSheetPage? sheet = null;

		async Task<bool> BeforeCreateAsync(TizenDistributorLevel level, CancellationToken ct)
		{
			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				sheet = new CertificateSheetPage(tvName, level);
				await navigation.PushModalAsync(sheet);
			});
			if (!await sheet!.SignInChosen)
				cancel.Cancel();
			cancel.Token.ThrowIfCancellationRequested();
			if (!sheet.SamsungInstead)
				return true;
			sheet = null;
			return false;
		}

		try
		{
			var outcome = await _provisioner.ProvisionTizenAsync(
				tvIp,
				requirePartner,
				message =>
				{
					report?.Invoke(message);
					MainThread.BeginInvokeOnMainThread(() => sheet?.Report(message));
				},
				BeforeCreateAsync,
				stage => MainThread.BeginInvokeOnMainThread(() => sheet?.SetStage(stage)),
				cancel.Token);

			if (outcome.SamsungInstead)
			{
				MobileSettings.CertificateKind = MobileSettings.CertificateKindSamsung;
				return null;
			}

			if (sheet is { } done)
			{
				await MainThread.InvokeOnMainThreadAsync(async () =>
				{
					done.SetStage(CertificateStage.Done);
					await Task.Delay(700);
					await done.CloseAsync();
				});
			}
			return (outcome.Result!, outcome.Created);
		}
		catch (Exception ex) when (sheet is { } open)
		{
			if (ex is OperationCanceledException)
				await MainThread.InvokeOnMainThreadAsync(open.CloseAsync);
			else
				MainThread.BeginInvokeOnMainThread(() => open.Fail(ErrorText.Describe(ex, "certificate")));
			throw;
		}
	}

	/// <summary>
	/// The TV refused the Tizen certificate (#988): the sheet with the TV's words and Sign in with
	/// Samsung instead. True when the user chose that: the setting is switched to Samsung, and the
	/// caller runs its certificate step again. False keeps the Tizen setting.
	/// </summary>
	public async Task<bool> ShowTizenRefusedAsync(INavigation navigation, string tvName, TizenCertificateProfile profile, string? reason)
	{
		CertificateSheetPage? sheet = null;
		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			sheet = new CertificateSheetPage(tvName, profile.Level);
			sheet.ShowRefused(profile.ProfileName, reason, CertificateProvisioningService.AuthorValidUntil(profile.ProfileDir));
			await navigation.PushModalAsync(sheet);
		});
		var chosen = await sheet!.SignInChosen && sheet.SamsungInstead;
		if (chosen)
			MobileSettings.CertificateKind = MobileSettings.CertificateKindSamsung;
		return chosen;
	}
}
