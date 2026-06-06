using System;
using System.IO;
using System.IO.Compression;

namespace Apps2Samsung.Helpers.Core
{
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
