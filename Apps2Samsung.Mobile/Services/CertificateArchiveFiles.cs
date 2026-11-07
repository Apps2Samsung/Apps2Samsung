using Apps2Samsung.Certificate;
using Apps2Samsung.Mobile.Localization;

namespace Apps2Samsung.Mobile.Services;

/// <summary>What the two Import certificate buttons (the certificate sheet, Settings > Backup) share (#921).</summary>
public static class CertificateArchiveFiles
{
	/// <summary>The outcome of an import that went through: whether it covers the TV in question.</summary>
	public sealed record Outcome(bool CoversTv, int FileCount);

	/// <summary>
	/// Picks the file, shows what it holds and what it replaces, imports on Import. Null when the user
	/// cancelled either step; throws <see cref="CertificateArchiveException"/> with the reason for a
	/// file that cannot be imported. The certificate preference follows the import.
	/// </summary>
	public static async Task<Outcome?> PickAndImportAsync(Page page, string? tvDuid)
	{
		// The archive's own extension is not a MIME type the pickers know; a file manager hands it
		// over as application/octet-stream, and a full backup .zip reads as an archive too.
		var picked = await LocalFilePicker.PickAsync("application/zip", "application/octet-stream");
		if (picked is null)
			return null;

		var store = new MobileAppConfig().CertificateStorePath;
		CertificateArchivePreview preview;
		using (var stream = File.OpenRead(picked.LocalPath))
			preview = CertificateArchive.Preview(stream, store);

		var text = CertificateArchive.Summarize(preview, tvDuid, L10n.Get);
		if (preview.IsEmpty)
			throw new CertificateArchiveException(text);

		if (!await page.DisplayAlert(L10n.Get("certArchiveConfirm"), text, L10n.Get("lblImport"), L10n.Get("lblCancel")))
			return null;

		CertificateArchiveImportResult result;
		using (var stream = File.OpenRead(picked.LocalPath))
			result = CertificateArchive.Import(stream, store);

		MobileSettings.CertificatePreference = CertificateArchive.PreferenceAfterImport(MobileSettings.CertificatePreference, store);
		return new Outcome(preview.Covers(tvDuid), result.FileCount);
	}
}
