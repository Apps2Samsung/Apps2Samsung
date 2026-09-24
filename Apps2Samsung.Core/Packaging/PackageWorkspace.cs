using System.Diagnostics;
using System.IO.Compression;
using System.Linq;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Extract-edit-repack helper for a <c>.wgt</c> package (a zip). Extracts to a temp dir next
    /// to the package, lets callers edit files under <see cref="Root"/>, then repacks in place.
    /// Portable (System.IO.Compression only) — shared by the desktop and mobile heads.
    /// <para>
    /// One install opens <b>one</b> workspace and every matching
    /// <see cref="Apps2Samsung.Interfaces.IPackagePatcher"/> edits it in turn, so a package is
    /// unpacked and rezipped once however many patchers apply to it.
    /// </para>
    /// </summary>
    public sealed class PackageWorkspace : IDisposable
    {
        public string Root { get; }

        /// <summary>
        /// The package this workspace was extracted from. Patchers decide what applies from the file
        /// name (a TVApp build, a Jellyfin build, the user's per-app icon and title choices), so they
        /// need it even though they never open it themselves.
        /// </summary>
        public string PackagePath => _originalPackage;

        private readonly string _originalPackage;
        private readonly string _tempPackage;
        private readonly (int Files, DateTime Newest) _asExtracted;

        private PackageWorkspace(string root, string original, string temp)
        {
            Root = root;
            _originalPackage = original;
            _tempPackage = temp;
            _asExtracted = Snapshot(root);
        }

        public static PackageWorkspace Extract(string packagePath)
        {
            var baseDir = Path.GetDirectoryName(packagePath)!;
            var tempDir = Path.Combine(baseDir, $"JellyTemp_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            ZipFile.ExtractToDirectory(packagePath, tempDir);
            return new PackageWorkspace(tempDir, packagePath, packagePath + ".tmp");
        }

        /// <summary>
        /// True once something under <see cref="Root"/> has been added, removed or written.
        /// <para>
        /// Extraction stamps every file with its zip entry's timestamp, which is when the package was
        /// built, so anything written during patching is unmistakably newer, and a file added or
        /// removed moves the count. That is enough to tell an edited package from one where every
        /// patcher looked and decided it had nothing to do (a Jellyfin install with no server
        /// configured, a TVApp build with no channels), which should be handed to the TV exactly as
        /// downloaded rather than as a recompressed copy of itself.
        /// </para>
        /// </summary>
        public bool HasChanges => Snapshot(Root) != _asExtracted;

        /// <summary>Repacks only if a patcher actually changed something. Returns whether it did.</summary>
        public bool RepackIfChanged()
        {
            if (!HasChanges)
            {
                Trace.WriteLine($"[Package] {Path.GetFileName(_originalPackage)} unchanged by the patchers; leaving it as downloaded.");
                return false;
            }

            Repack();
            return true;
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
                $"{Snapshot(Root).Files} files, {repacked.Length:N0} bytes.");
        }

        private static (int Files, DateTime Newest) Snapshot(string root)
        {
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            var newest = files.Count == 0
                ? DateTime.MinValue
                : files.Max(File.GetLastWriteTimeUtc);

            return (files.Count, newest);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
            try { if (File.Exists(_tempPackage)) File.Delete(_tempPackage); } catch { }
        }
    }
}
