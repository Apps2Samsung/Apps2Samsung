using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin2Samsung.Helpers.Core
{
    /// <summary>
    /// Provides centralized platform detection and platform-specific operations.
    /// Eliminates duplicate OperatingSystem.* and RuntimeInformation.* checks throughout the codebase.
    /// </summary>
    public static class PlatformService
    {
        /// <summary>
        /// Represents the current operating system platform.
        /// </summary>
        public enum Platform
        {
            Windows,
            Linux,
            MacOS,
            Unknown
        }

        /// <summary>
        /// Gets the current platform.
        /// </summary>
        public static Platform CurrentPlatform
        {
            get
            {
                if (OperatingSystem.IsWindows())
                    return Platform.Windows;
                if (OperatingSystem.IsLinux())
                    return Platform.Linux;
                if (OperatingSystem.IsMacOS())
                    return Platform.MacOS;
                return Platform.Unknown;
            }
        }

        /// <summary>
        /// Returns true if the current platform is Windows.
        /// </summary>
        public static bool IsWindows => OperatingSystem.IsWindows();

        /// <summary>
        /// Returns true if the current platform is Linux.
        /// </summary>
        public static bool IsLinux => OperatingSystem.IsLinux();

        /// <summary>
        /// Returns true if the current platform is macOS.
        /// </summary>
        public static bool IsMacOS => OperatingSystem.IsMacOS();

        /// <summary>
        /// Returns true if the current platform is Unix-like (Linux or macOS).
        /// </summary>
        public static bool IsUnixLike => IsLinux || IsMacOS;

        /// <summary>
        /// Returns true if the current process architecture is ARM64.
        /// </summary>
        public static bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        /// <summary>
        /// Gets the appropriate TizenSdb search pattern for the current platform.
        /// </summary>
        /// <returns>The file search pattern for TizenSdb binary.</returns>
        /// <exception cref="PlatformNotSupportedException">Thrown when the platform is not supported.</exception>
        public static string GetTizenSdbSearchPattern()
        {
            return CurrentPlatform switch
            {
                Platform.Windows => Constants.PlatformBinaries.TizenSdbWindowsPattern,
                Platform.Linux => IsArm64
                    ? Constants.PlatformBinaries.TizenSdbLinuxArm64Pattern
                    : Constants.PlatformBinaries.TizenSdbLinuxX64Pattern,
                Platform.MacOS => IsArm64
                    ? Constants.PlatformBinaries.TizenSdbMacOsArm64Pattern
                    : Constants.PlatformBinaries.TizenSdbMacOsPattern,
                _ => throw new PlatformNotSupportedException("Unsupported operating system")
            };
        }

        /// <summary>
        /// Gets the TizenSdb file name with version for the current platform.
        /// </summary>
        /// <param name="version">The version string to include in the file name.</param>
        /// <returns>The platform-specific file name.</returns>
        /// <exception cref="PlatformNotSupportedException">Thrown when the platform is not supported.</exception>
        public static string GetTizenSdbFileName(string version)
        {
            return CurrentPlatform switch
            {
                Platform.Windows => $"TizenSdb_{version}{Constants.PlatformBinaries.WindowsExtension}",
                Platform.Linux => $"TizenSdb_{version}{(IsArm64
                    ? Constants.PlatformBinaries.LinuxArm64Suffix
                    : Constants.PlatformBinaries.LinuxX64Suffix)}",
                Platform.MacOS => $"TizenSdb_{version}{(IsArm64
                    ? Constants.PlatformBinaries.MacArm64Suffix
                    : Constants.PlatformBinaries.MacX64Suffix)}",
                _ => throw new PlatformNotSupportedException("Unsupported operating system")
            };
        }

        /// <summary>
        /// Gets the platform identifier for matching GitHub release assets.
        /// </summary>
        /// <returns>The platform identifier string used in asset file names.</returns>
        /// <exception cref="PlatformNotSupportedException">Thrown when the platform is not supported.</exception>
        public static string GetAssetPlatformIdentifier()
        {
            return CurrentPlatform switch
            {
                Platform.Windows => "exe",
                Platform.Linux => IsArm64 ? "linux-arm64" : "linux-x64",
                Platform.MacOS => IsArm64 ? "macos-arm64" : "macos-x64",
                _ => throw new PlatformNotSupportedException("Unsupported operating system")
            };
        }

        /// <summary>
        /// Gets the esbuild binary path relative to the esbuild directory.
        /// </summary>
        /// <param name="esbuildBasePath">The base path to the esbuild directory.</param>
        /// <returns>The full path to the esbuild binary, or null if not supported.</returns>
        public static string? GetEsbuildPath(string esbuildBasePath)
        {
            string relativePath = CurrentPlatform switch
            {
                Platform.Windows => Path.Combine(
                    Constants.PlatformBinaries.EsbuildWindows,
                    Constants.PlatformBinaries.EsbuildExecutableWindows),
                Platform.Linux => Path.Combine(
                    Constants.PlatformBinaries.EsbuildLinux,
                    Constants.PlatformBinaries.EsbuildExecutable),
                Platform.MacOS => Path.Combine(
                    Constants.PlatformBinaries.EsbuildMacOs,
                    Constants.PlatformBinaries.EsbuildExecutable),
                _ => null
            };

            if (relativePath == null)
                return null;

            string fullPath = Path.Combine(esbuildBasePath, relativePath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        /// <summary>
        /// Gets the ARP command arguments for the current platform.
        /// </summary>
        /// <param name="ipAddress">The IP address to query.</param>
        /// <returns>The ARP command arguments.</returns>
        public static string GetArpArguments(string ipAddress)
        {
            return IsWindows ? $"-a {ipAddress}" : $"-n {ipAddress}";
        }

        /// <summary>
        /// Gets the appropriate X509KeyStorageFlags for the current platform.
        /// </summary>
        /// <returns>The platform-appropriate key storage flags.</returns>
        public static X509KeyStorageFlags GetX509KeyStorageFlags()
        {
            return IsWindows
                ? X509KeyStorageFlags.EphemeralKeySet
                : X509KeyStorageFlags.PersistKeySet;
        }

        /// <summary>
        /// Gets firewall configuration help text for the current platform.
        /// </summary>
        /// <param name="port">The port number to include in instructions.</param>
        /// <returns>Platform-specific firewall configuration instructions.</returns>
        public static string GetFirewallHelpText(int port)
        {
            return CurrentPlatform switch
            {
                Platform.Windows =>
                    $"Windows:\n" +
                    $"  netstat -ano | findstr {port}\n\n" +
                    $"  New-NetFirewallRule -DisplayName \"Apps2Samsung Logs\" \\\n" +
                    $"    -Direction Inbound -Protocol TCP -LocalPort {port} -Action Allow\n",

                Platform.Linux =>
                    $"Linux:\n  sudo ufw allow {port}/tcp\n  sudo ufw reload\n",

                Platform.MacOS =>
                    "macOS:\n" +
                    "  System Settings -> Network -> Firewall -> Options\n" +
                    "  Allow incoming connections for Apps2Samsung\n",

                _ => "Ensure your firewall allows inbound TCP connections.\n"
            };
        }

        /// <summary>
        /// Determines if the file needs executable permissions set (Unix-like systems only).
        /// </summary>
        /// <returns>True if chmod is needed, false otherwise.</returns>
        public static bool RequiresExecutablePermissions()
        {
            return IsUnixLike;
        }
    }
}
