using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Apps2Samsung.Certificate;
using Apps2Samsung.Helpers;

namespace Apps2Samsung.Views
{
    /// <summary>What the two Import certificate buttons (the certificate sheet, Settings > Backup) share (#921).</summary>
    public static class CertificateArchiveFiles
    {
        /// <summary>The outcome of an import that went through: whether it covers the TV in question.</summary>
        public sealed record Outcome(bool CoversTv, int FileCount);

        /// <summary>The archive's own extension plus .zip, since a full backup also reads as one.</summary>
        public static FilePickerFileType[] PickerTypes(Func<string, string> localize) => new[]
        {
            new FilePickerFileType(localize("lblCertificateArchive"))
            {
                Patterns = new[] { "*" + CertificateArchive.FileExtension, "*.zip" },
            },
        };

        /// <summary>
        /// Reads the file, shows the preview through <paramref name="confirm"/> (title, text) and
        /// imports on yes. Null when the file holds nothing or the user declined; throws
        /// <see cref="CertificateArchiveException"/> for an archive that cannot be imported, with the
        /// reason. The certificate preference follows the import, as on the phone.
        /// </summary>
        public static async Task<Outcome?> ImportWithPreviewAsync(
            IStorageFile file, string? tvDuid, Func<string, string> localize, Func<string, string, Task<bool>> confirm)
        {
            using var buffer = new MemoryStream();
            await using (var source = await file.OpenReadAsync())
                await source.CopyToAsync(buffer);

            buffer.Position = 0;
            var preview = CertificateArchive.Preview(buffer, AppSettings.CertificatePath);
            var text = CertificateArchive.Summarize(preview, tvDuid, localize);
            if (preview.IsEmpty)
                throw new CertificateArchiveException(text);

            if (!await confirm(localize("certArchiveConfirm"), text))
                return null;

            buffer.Position = 0;
            var result = CertificateArchive.Import(buffer, AppSettings.CertificatePath);

            AppSettings.Default.CertificatePreference = CertificateArchive.PreferenceAfterImport(
                AppSettings.Default.CertificatePreference, AppSettings.CertificatePath);
            AppSettings.Default.Save();

            return new Outcome(preview.Covers(tvDuid), result.FileCount);
        }
    }
}
