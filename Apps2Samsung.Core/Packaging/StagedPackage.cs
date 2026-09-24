using System;
using System.IO;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// A throwaway copy of a <c>.wgt</c> for one install attempt.
    /// <para>
    /// The install pipeline rewrites the package in place: every <see cref="Apps2Samsung.Interfaces.IPackagePatcher"/>
    /// extracts and repacks it, and the re-sign replaces its signatures. The file the caller hands in must
    /// never be the one that gets rewritten. The downloaded package is a cache that is reused by the next
    /// install (and kept for good with "Preserve WGT file"), and a custom package is a file the user picked
    /// themselves. Patching an already-patched copy stacks the injections: a second auto-login block, a
    /// second custom-CSS style, another <c>allow-mixed-content</c> element, with no way back short of
    /// deleting the file by hand (#702).
    /// </para>
    /// <para>
    /// So each attempt works on a copy under the OS temp directory, and the caller's file stays pristine.
    /// </para>
    /// </summary>
    public sealed class StagedPackage : IDisposable
    {
        private static readonly string StagingRoot =
            Path.Combine(Path.GetTempPath(), "Apps2Samsung", "staging");

        /// <summary>Full path of the working copy. This is the file to patch, sign and push.</summary>
        public string FilePath { get; }

        private readonly string _directory;

        private StagedPackage(string filePath, string directory)
        {
            FilePath = filePath;
            _directory = directory;
        }

        /// <summary>Copies <paramref name="sourcePackage"/> into a private staging directory.</summary>
        public static StagedPackage Create(string sourcePackage)
        {
            SweepStale();

            var directory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            // Same file name: patchers match on it (CanHandle), and the installed app's title is
            // derived from it, so a staged copy has to keep the name the caller downloaded.
            var staged = Path.Combine(directory, Path.GetFileName(sourcePackage));
            File.Copy(sourcePackage, staged, overwrite: true);

            return new StagedPackage(staged, directory);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
        }

        // A crash (or a kill from the task manager) skips Dispose and leaves a copy of the package
        // behind, so drop yesterday's leftovers whenever a new one is staged. Best effort: a directory
        // another instance is using right now stays where it is.
        private static void SweepStale()
        {
            try
            {
                if (!Directory.Exists(StagingRoot))
                    return;

                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var directory in Directory.EnumerateDirectories(StagingRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                            Directory.Delete(directory, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
