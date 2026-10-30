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
	/// was made (the sheet was shown), false when an existing one was reused.
	/// </summary>
	public async Task<(CertificateProvisioner.Result Result, bool Created)> RunAsync(
		INavigation navigation,
		string tvIp,
		string tvName,
		bool requirePartner,
		Action<string>? report,
		CancellationTokenSource cancel)
	{
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
				cancel.Token);

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
}
