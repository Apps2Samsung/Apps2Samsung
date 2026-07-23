using System;
using System.Threading.Tasks;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;

namespace Apps2Samsung.Sdb
{
    /// <summary>
    /// A TV's <c>sdb capability</c> report reduced to the two fields the install path needs. Shared by
    /// both heads: the desktop parsed this with regex while the mobile head hand-rolled its own
    /// line splitter (and hardcoded the default path) — both parsed the <b>same</b> report, since the
    /// mobile in-process engine deliberately shapes its output to match the desktop CLI's.
    /// </summary>
    public readonly record struct TizenCapabilities(string PlatformVersion, string SdkToolPath)
    {
        /// <summary>
        /// Parse a capability report. A missing <c>platform_version</c> yields an empty string (callers
        /// treat that as "unknown"); a missing <c>sdk_toolpath</c> falls back to the default staging path.
        /// </summary>
        public static TizenCapabilities Parse(string? capabilityOutput)
        {
            var output = capabilityOutput ?? string.Empty;

            var versionMatch = RegexPatterns.TizenCapability.PlatformVersion.Match(output);
            var platformVersion = versionMatch.Success ? versionMatch.Groups[1].Value.Trim() : string.Empty;

            var pathMatch = RegexPatterns.TizenCapability.SdkToolPath.Match(output);
            var sdkToolPath = pathMatch.Success ? pathMatch.Groups[1].Value.Trim() : Constants.Defaults.SdkToolPath;

            return new TizenCapabilities(platformVersion, sdkToolPath);
        }

        /// <summary>The parsed platform version, or <c>null</c> when the report had none/was unparseable.</summary>
        public Version? Version => System.Version.TryParse(PlatformVersion, out var v) ? v : null;
    }

    /// <summary>
    /// The pre-install "permit" step. Older Samsung TVs must have the distributor device profile pushed
    /// before an install is accepted; newer TVs carry the authorization inside the re-signed package.
    /// Centralizes the version thresholds and target-path rule so both heads agree — the mobile head
    /// previously kept its own hardcoded copies of these (4.0 / 3.0 / /home/developer).
    /// </summary>
    public static class TizenPermitInstall
    {
        private static readonly Version PushInstallMax = new(Constants.TizenVersions.PushInstallMax);
        private static readonly Version IntermediateVersion = new(Constants.TizenVersions.IntermediateVersion);

        /// <summary>True when the TV needs the device profile pushed before install (version ≤ 4.0).</summary>
        public static bool IsRequired(Version? platformVersion) =>
            platformVersion is not null && platformVersion <= PushInstallMax;

        /// <summary>Staging path for the profile push: intermediate TVs (&lt; 3.0) use the fixed
        /// developer home, otherwise the TV's reported SDK tool path.</summary>
        public static string TargetPath(Version platformVersion, string sdkToolPath) =>
            platformVersion < IntermediateVersion ? Constants.Defaults.HomeDeveloperPath : sdkToolPath;

        /// <summary>Push the distributor device profile when the TV requires it; a no-op otherwise.</summary>
        public static async Task EnsureAsync(
            ISdbEngine sdb, string tvIpAddress, Version? platformVersion, string sdkToolPath, string deviceProfilePath)
        {
            if (!IsRequired(platformVersion))
                return;

            await sdb.PermitInstallAsync(tvIpAddress, deviceProfilePath, TargetPath(platformVersion!, sdkToolPath));
        }
    }
}
