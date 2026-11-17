using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Update
{
    /// <summary>
    /// The file side of an update, shared by the heads that replace themselves: unpacking a release
    /// archive (the desktop and the CLI) and putting the new files over the running ones (the CLI,
    /// which has no window to restart and so needs no helper script).
    /// </summary>
    public static class UpdateFiles
    {
        /// <summary>What a replaced file that was still in use is renamed to on Windows, until <see cref="RemoveLeftovers"/> can delete it.</summary>
        public const string LeftoverExtension = ".a2s-old";

        /// <summary>Unpacks a release archive: Windows ships .zip, everything else .tar.gz (which keeps the executable bit).</summary>
        public static async Task ExtractAsync(string archivePath, string destinationDir, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDir);
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Off the caller's thread: an 87 MB archive takes seconds, and a frozen window reads as stuck.
                await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, destinationDir, overwriteFiles: true), cancellationToken);
            }
            else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                     archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                await using var fileStream = File.OpenRead(archivePath);
                await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                // TarFile preserves Unix file permissions (e.g. the executable bit) on extraction.
                await TarFile.ExtractToDirectoryAsync(gzipStream, destinationDir, overwriteFiles: true, cancellationToken);
            }
            else
            {
                throw new NotSupportedException("Only ZIP and TAR.GZ archives are supported for automatic updates.");
            }
        }

        /// <summary>
        /// Moves every file under <paramref name="sourceDir"/> to the same place under
        /// <paramref name="targetDir"/>, over what is there, and returns how many. Files that are
        /// only in the target stay: the target may be a folder the user keeps other things in.
        /// Both must be on one volume, so each file is a rename and never half written.
        /// <para>
        /// A program can replace its own files this way while it runs. On Linux, macOS and Android
        /// a rename over an open file leaves the running copy alone. Windows refuses to overwrite a
        /// running executable but lets it be renamed, so the old file steps aside as
        /// <c>&lt;name&gt;.&lt;random&gt;.a2s-old</c> and <see cref="RemoveLeftovers"/> deletes it on a later start.
        /// </para>
        /// </summary>
        public static int Replace(string sourceDir, string targetDir)
        {
            var count = 0;
            foreach (var source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetDir, Path.GetRelativePath(sourceDir, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ReplaceFile(source, target);
                count++;
            }
            return count;
        }

        private static void ReplaceFile(string source, string target)
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(target))
            {
                File.Move(source, target, overwrite: true);
                return;
            }

            var aside = $"{target}.{Guid.NewGuid():N}"[..(target.Length + 9)] + LeftoverExtension;
            File.Move(target, aside);
            try
            {
                File.Move(source, target);
            }
            catch
            {
                File.Move(aside, target);
                throw;
            }
            try
            {
                File.Delete(aside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still running: RemoveLeftovers takes it on a later start.
            }
        }

        /// <summary>Deletes what <see cref="Replace"/> had to leave behind in <paramref name="dir"/>; whatever is still in use stays for the next time.</summary>
        public static void RemoveLeftovers(string dir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*" + LeftoverExtension))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
