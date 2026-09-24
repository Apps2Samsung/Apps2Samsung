using System.Diagnostics;
using System.IO.Compression;
using System.Linq;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Extract-edit-repack helper for a <c>.wgt</c> package (a zip). Extracts to a temp dir next
    /// to the package, lets callers edit files under <see cref="Root"/>, then repacks in place.
    /// Portable (System.IO.Compression only) — shared by the desktop and mobile heads.
    /// </summary>
    public sealed class PackageWorkspace : IDisposable
    {
        public string Root { get; }
        private readonly string _originalPackage;
        private readonly string _tempPackage;

        private PackageWorkspace(string root, string original, string temp)
        {
            Root = root;
            _originalPackage = original;
            _tempPackage = temp;
        }

        public static PackageWorkspace Extract(string packagePath)
        {
            var baseDir = Path.GetDirectoryName(packagePath)!;
            var tempDir = Path.Combine(baseDir, $"JellyTemp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            ZipFile.ExtractToDirectory(packagePath, tempDir);
            return new PackageWorkspace(tempDir, packagePath, packagePath + ".tmp");
        }

        public void Repack()
        {
            if (File.Exists(_tempPackage))
                File.Delete(_tempPackage);

            // SmallestSize, not the default Optimal: the packages we repack were built with a stronger
            // deflate than .NET's default, so repacking at Optimal handed the TV a bigger file than the
            // one that was downloaded (measured on a 10.8 MB Jellyfin build: +110 KB at Optimal against
            // +3.8 KB here, for ~0.8 s more on the one repack an install does). TV app storage is the
            // scarce resource, not the second.
            ZipFile.CreateFromDirectory(Root, _tempPackage, CompressionLevel.SmallestSize, includeBaseDirectory: false);

            // Overwrite in one move rather than delete-then-move: a crash (or a kill) between the two
            // used to leave no package at all, and the next run then re-downloaded silently.
            File.Move(_tempPackage, _originalPackage, overwrite: true);

            // The size of a repacked package is the cheapest signal that a patch did something odd —
            // it is the number that grows run over run when injections stack up (#702) — and a bug
            // report is the only place anyone ever sees it.
            var repacked = new FileInfo(_originalPackage);
            Trace.WriteLine(
                $"[Package] Repacked {Path.GetFileName(_originalPackage)}: " +
                $"{Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Count()} files, {repacked.Length:N0} bytes.");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
            try { if (File.Exists(_tempPackage)) File.Delete(_tempPackage); } catch { }
        }
    }
}
