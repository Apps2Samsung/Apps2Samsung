using System;
using System.IO;

namespace Apps2Samsung.Portability
{
    /// <summary>
    /// Where the app may write on the current OS. The install directory is NOT such a place: on macOS
    /// writing into the signed .app bundle breaks its signature and the TCC prompt (#498), and on Linux
    /// a package can be installed read-only — /opt owned by root for a .deb or .rpm, and a squashfs
    /// mount for an AppImage, where nothing next to the binary is writable at all (#589).
    /// <para>
    /// Linux follows the XDG base directories, honouring XDG_CACHE_HOME / XDG_STATE_HOME when set;
    /// macOS uses ~/Library; Windows keeps writing next to the binary, which is where existing installs
    /// already have their files.
    /// </para>
    /// </summary>
    public static class UserPaths
    {
        private const string AppFolder = "Apps2Samsung";

        /// <summary>Throwaway downloads (the .wgt cache): XDG cache on Linux, ~/Library/Caches on macOS.</summary>
        public static string Cache(string installDirectory, params string[] parts) =>
            Combine(BaseCache(installDirectory), parts);

        /// <summary>Data worth keeping but not user-editable, e.g. logs: XDG state on Linux.</summary>
        public static string State(string installDirectory, params string[] parts) =>
            Combine(BaseState(installDirectory), parts);

        private static string BaseCache(string installDirectory)
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home(), "Library", "Caches", AppFolder);

            if (OperatingSystem.IsLinux())
                return Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), AppFolder);

            // Windows: unchanged — existing installs keep their folders next to the binary.
            return installDirectory;
        }

        private static string BaseState(string installDirectory)
        {
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home(), "Library", "Logs", AppFolder);

            if (OperatingSystem.IsLinux())
                return Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), AppFolder);

            return installDirectory;
        }

        // An absolute XDG_* wins; anything else (unset, or a relative value, which the spec says to
        // ignore) falls back to the default under $HOME.
        private static string Xdg(string variable, string fallbackRelativeToHome)
        {
            var configured = Environment.GetEnvironmentVariable(variable);
            return !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(Home(), fallbackRelativeToHome);
        }

        private static string Home()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // A service-like environment with no HOME shouldn't crash the app over a log path.
            return string.IsNullOrEmpty(home) ? Path.GetTempPath() : home;
        }

        private static string Combine(string root, string[] parts)
        {
            if (parts is null || parts.Length == 0)
                return root;

            var all = new string[parts.Length + 1];
            all[0] = root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }
    }
}
