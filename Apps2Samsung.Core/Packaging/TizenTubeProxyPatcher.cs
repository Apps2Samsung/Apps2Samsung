using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Apps2Samsung.Configuration;
using Apps2Samsung.Helpers.Core; // PackageWorkspace
using Apps2Samsung.Interfaces;    // IPackagePatcher
using Apps2Samsung.Models;        // InstallResult

namespace Apps2Samsung.Packaging
{
    /// <summary>
    /// Opt-in fix for TizenTube Cobalt on Tizen 9: points the native Cobalt player at the TV's LAN
    /// address instead of the loopback one the package ships with.
    /// <para>
    /// The Cobalt builds carry the player's command line in <c>config.xml</c>
    /// (<c>native.userdata</c>), including <c>--proxy=http://127.0.0.2:8101</c> (or
    /// <c>http://tizentube:8101</c> in the ProxyAddress build). TizenTube's own service listens on
    /// <c>0.0.0.0:8101</c>, so it is reachable on the TV's LAN address too, but on Tizen 9 Cobalt
    /// fails to reach it on 127.0.0.2 and the app opens on "A network error has occurred".
    /// Rewriting the host to the TV's own IP is the workaround users were doing by hand
    /// (unzip, edit, rezip, install as a custom WGT).
    /// </para>
    /// <para>
    /// Off by default: the address is baked into the package, so the app breaks again if the TV's
    /// IP changes, and the TV needs a DHCP reservation for this to stick. When no host is
    /// configured the patcher uses the IP of the TV being installed to.
    /// </para>
    /// </summary>
    public sealed class TizenTubeProxyPatcher : IPackagePatcher
    {
        // The value of the proxy flag inside native.userdata: scheme, host and optional port.
        private static readonly Regex ProxyFlagRegex = new(
            @"--proxy=(?<scheme>https?://)(?<host>[^\s:/""]+)(?::(?<port>\d+))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Host (IPv4 or DNS name) with an optional :port, as a user may type it.
        private static readonly Regex HostInputRegex = new(
            @"^(?<host>[A-Za-z0-9](?:[A-Za-z0-9.\-]*[A-Za-z0-9])?)(?::(?<port>\d{1,5}))?$",
            RegexOptions.Compiled);

        private readonly IAppConfig _config;

        public TizenTubeProxyPatcher(IAppConfig config) => _config = config;

        public bool CanHandle(string packagePath) =>
            _config.TizenTubeProxyOverride && IsCobaltPackage(packagePath);

        public async Task<InstallResult> ApplyAsync(PackageWorkspace ws)
        {
            var configPath = Path.Combine(ws.Root, "config.xml");
            if (!File.Exists(configPath))
                return InstallResult.SuccessResult();

            var configured = _config.TizenTubeProxyHost?.Trim();
            var input = string.IsNullOrEmpty(configured) ? ws.TargetDeviceIp : configured;
            if (!TryParseHost(input, out var host, out var port))
            {
                Trace.WriteLine($"[TizenTube] No usable proxy host ('{input}'); leaving the package's proxy address unchanged.");
                return InstallResult.SuccessResult();
            }

            var xml = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
            var match = ProxyFlagRegex.Match(xml);
            if (!match.Success)
            {
                Trace.WriteLine("[TizenTube] No --proxy flag in config.xml; leaving the package unchanged.");
                return InstallResult.SuccessResult();
            }

            // Keep the package's own port unless the user typed one: it is the port TizenTube's
            // service listens on, and it may change between releases.
            var effectivePort = port ?? (match.Groups["port"].Success ? match.Groups["port"].Value : null);
            var replacement = $"--proxy={match.Groups["scheme"].Value}{host}" +
                              (effectivePort is null ? string.Empty : $":{effectivePort}");

            if (string.Equals(match.Value, replacement, StringComparison.OrdinalIgnoreCase))
                return InstallResult.SuccessResult();

            xml = ProxyFlagRegex.Replace(xml, _ => replacement, 1);
            await File.WriteAllTextAsync(configPath, xml, new UTF8Encoding(false));

            Trace.WriteLine($"[TizenTube] Cobalt proxy rewritten: '{match.Value}' -> '{replacement}'.");
            return InstallResult.SuccessResult();
        }

        /// <summary>
        /// Validates a host as typed in the settings (an IP or name, optionally with <c>:port</c>).
        /// Empty is valid there (it means "the TV being installed to"), so callers check that first.
        /// </summary>
        public static bool TryParseHost(string? input, out string host, out string? port)
        {
            host = string.Empty;
            port = null;

            var match = HostInputRegex.Match(input?.Trim() ?? string.Empty);
            if (!match.Success)
                return false;

            if (match.Groups["port"].Success)
            {
                if (!int.TryParse(match.Groups["port"].Value, out var number) || number is < 1 or > 65535)
                    return false;
                port = number.ToString();
            }

            host = match.Groups["host"].Value;
            return true;
        }

        /// <summary>True for a TizenTube Cobalt build: its config.xml passes Cobalt a --proxy flag.</summary>
        private static bool IsCobaltPackage(string packagePath)
        {
            try
            {
                using var archive = ZipFile.OpenRead(packagePath);
                var entry = archive.GetEntry("config.xml");
                if (entry is null)
                    return false;

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var xml = reader.ReadToEnd();
                return xml.Contains("native.userdata", StringComparison.Ordinal) && ProxyFlagRegex.IsMatch(xml);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
