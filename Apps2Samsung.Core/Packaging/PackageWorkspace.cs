using System.IO.Compression;

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

            ZipFile.CreateFromDirectory(Root, _tempPackage);
            File.Delete(_originalPackage);
            File.Move(_tempPackage, _originalPackage);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
            try { if (File.Exists(_tempPackage)) File.Delete(_tempPackage); } catch { }
        }
    }
}
