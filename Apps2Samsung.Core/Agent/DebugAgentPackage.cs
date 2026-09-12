using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;

namespace Apps2Samsung.Agent
{
    /// <summary>
    /// The Apps2Samsung Debug agent as a package: a tiny Tizen web app whose three files ship inside
    /// this assembly (see <c>Agent/Wgt/</c>) and are zipped into a <c>.wgt</c> on demand, so both
    /// heads install it through the pipeline every other package goes through — certificate, resign,
    /// push — with nothing to download and no version drift between the app and the agent it drives.
    /// <para>
    /// The package is unsigned on purpose: the resign step strips whatever signature files a package
    /// carries and writes fresh ones, so there is nothing to keep in sync.
    /// </para>
    /// </summary>
    public static class DebugAgentPackage
    {
        /// <summary>The Tizen package id, as <c>vd_applist</c> lists it under <c>app_package_name</c>.</summary>
        public const string PackageId = "A2SDebug01";

        /// <summary>The Tizen application id: what <c>0 debug</c> and <c>0 was_kill</c> address.</summary>
        public const string AppId = "A2SDebug01.Agent";

        /// <summary>The agent version this build embeds — <c>A2S.version</c> inside the running agent.</summary>
        public const string Version = "0.2.0";

        /// <summary>The file name the package is written under.</summary>
        public const string FileName = "Apps2SamsungDebug.wgt";

        private const string ResourcePrefix = "Apps2Samsung.Core.DebugAgent.";
        private static readonly string[] Files = { "config.xml", "index.html", "icon.png" };

        /// <summary>
        /// Writes the agent's <c>.wgt</c> into <paramref name="directory"/> (created if needed) and
        /// returns its path. Overwrites a previous copy: the file is a build artefact, not a download.
        /// </summary>
        public static async Task<string> WriteAsync(string directory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, FileName);
            if (File.Exists(path))
                File.Delete(path);

            var assembly = typeof(DebugAgentPackage).Assembly;
            await using var output = File.Create(path);
            using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var file in Files)
            {
                await using var source = assembly.GetManifestResourceStream(ResourcePrefix + file)
                    ?? throw new InvalidOperationException($"The debug agent's {file} is not embedded in this build.");

                // Stored, not deflated: the TV unpacks the package in a moment either way, and the
                // signer hashes the raw bytes it reads back, so compression buys nothing here.
                var entry = zip.CreateEntry(file, CompressionLevel.NoCompression);
                await using var target = entry.Open();
                await source.CopyToAsync(target);
            }

            return path;
        }

        /// <summary>A scratch directory for the package, under the platform's temp path.</summary>
        public static string DefaultDirectory =>
            Path.Combine(Path.GetTempPath(), "Apps2Samsung", "debug-agent");
    }
}
