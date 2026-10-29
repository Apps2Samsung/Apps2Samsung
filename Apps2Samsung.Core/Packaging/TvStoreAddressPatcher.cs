using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Writes the TV's own IP into TV-Store (#844) at install, in place of the loopback address the
    /// page uses to reach its background service (<c>http://127.0.0.1:8077</c> in <c>config.xml</c>'s
    /// content security policy and in <c>js/app.js</c>).
    /// <para>
    /// On Tizen 10 a page may not reach its own service over loopback (TV-Store and Vetro both stopped
    /// at "service not coming up"; the TizenTube Cobalt fix in <see cref="TizenTubeProxyPatcher"/> is
    /// the same problem). The service listens on every interface and only answers this TV itself.
    /// TV-Store 0.3.6 and newer also try the TV's current IP and loopback when the written address
    /// stops answering, so a new DHCP lease does not break it.
    /// </para>
    /// </summary>
    public sealed class TvStoreAddressPatcher : IPackagePatcher
    {
        /// <summary>The address TV-Store ships with, in both files.</summary>
        public const string LoopbackBase = "http://127.0.0.1:8077";

        private static readonly string[] Files = { "config.xml", Path.Combine("js", "app.js") };

        public bool CanHandle(string packagePath) => IsTvStorePackage(packagePath);

        public async Task<InstallResult> ApplyAsync(PackageWorkspace ws)
        {
            if (!IPAddress.TryParse(ws.TargetDeviceIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
            {
                Trace.WriteLine($"[TV-Store] No usable TV IP ('{ws.TargetDeviceIp}'); leaving the service address on loopback.");
                return InstallResult.SuccessResult();
            }

            var replacement = $"http://{ip}:8077";
            foreach (var file in Files)
            {
                var path = Path.Combine(ws.Root, file);
                if (!File.Exists(path))
                    continue;
                var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
                if (!text.Contains(LoopbackBase, StringComparison.Ordinal))
                    continue;
                await File.WriteAllTextAsync(path, text.Replace(LoopbackBase, replacement, StringComparison.Ordinal), new UTF8Encoding(false));
                Trace.WriteLine($"[TV-Store] {file}: service address {LoopbackBase} -> {replacement}.");
            }
            return InstallResult.SuccessResult();
        }

        /// <summary>
        /// True for a TV-Store build: its config.xml declares the TV-Store package id, or, once an
        /// install under a new package id (#839) has rewritten that, still TV-Store's widget id (its
        /// repository URL) or its name. The rename runs before the patchers are picked, and a renamed
        /// TV-Store left on loopback never reaches its service on Tizen 10.
        /// </summary>
        public static bool IsTvStorePackage(string packagePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(packagePath);
                var entry = archive.GetEntry("config.xml");
                if (entry is null)
                    return false;

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var config = reader.ReadToEnd();
                return config.Contains($"package=\"{TvStorePackage.PackageId}\"", StringComparison.Ordinal)
                    || config.Contains($"id=\"{TvStorePackage.RepositoryUrl}\"", StringComparison.OrdinalIgnoreCase)
                    || config.Contains($"<name>{TvStorePackage.Title}</name>", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
