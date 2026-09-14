using Apps2Samsung.Helpers.Core;
using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Apps2Samsung.Helpers.API
{
    /// <summary>
    /// The identity this installer presents to a Jellyfin server in the "MediaBrowser" Authorization
    /// header. Jellyfin keys a device record (and the access token it hands out) on user + DeviceId,
    /// so the id has to be stable for one install and different between installs: with a single
    /// hard-coded id every copy of the installer shared one token per user, and a logout or a
    /// "revoke device" on one machine silently broke the others.
    /// <para>
    /// The move from the legacy X-Emby-Authorization header to this one (#655) was diagnosed and first
    /// written by Zach J Murphy (zacjmurphy) in his fork Apps2Samsung-Jellyfin12; #658 adapted it to
    /// Core.
    /// </para>
    /// </summary>
    public static class JellyfinClientIdentity
    {
        /// <summary>Client name shown in the Jellyfin dashboard's device list.</summary>
        public static string Client => Constants.Api.MediaBrowserClientName;

        /// <summary>
        /// Device name shown next to the client in the dashboard. The machine running the installer,
        /// not the TV: the TV gets its own session once the Jellyfin app on it signs in.
        /// </summary>
        public static string Device { get; } = ResolveDeviceName();

        /// <summary>
        /// Stable per-install id, derived from the machine and user names so it needs no storage and
        /// survives a reinstall. Two installs on the same machine for the same OS user share it,
        /// which is the same thing Jellyfin does for two browser profiles.
        /// </summary>
        public static string DeviceId { get; } = ResolveDeviceId();

        /// <summary>App version reported to the server; informational only.</summary>
        public static string Version { get; } = ResolveVersion();

        /// <summary>
        /// Builds the Authorization header value. Without a token it identifies the client for
        /// /Users/AuthenticateByName; with one it authenticates every later request.
        /// </summary>
        public static string AuthorizationHeader(string? accessToken = null)
        {
            return string.IsNullOrEmpty(accessToken)
                ? string.Format(Constants.Api.MediaBrowserAuthHeader, Client, Device, DeviceId, Version)
                : string.Format(Constants.Api.MediaBrowserAuthHeaderWithToken, Client, Device, DeviceId, Version, accessToken);
        }

        private static string ResolveDeviceName()
        {
            string name;
            try
            {
                name = Environment.MachineName;
            }
            catch
            {
                name = string.Empty;
            }

            // Android reports "localhost"; fall back to the platform so the dashboard entry still says something.
            if (string.IsNullOrWhiteSpace(name) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                name = OperatingSystem.IsAndroid() ? "Android" : Environment.OSVersion.Platform.ToString();

            // The header is a quoted, comma-separated parameter list; keep the name from breaking it.
            return name.Replace("\"", string.Empty).Replace(",", string.Empty).Trim();
        }

        private static string ResolveDeviceId()
        {
            var seed = $"{Constants.Api.MediaBrowserClientName}|{Environment.MachineName}|{Environment.UserName}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }

        private static string ResolveVersion()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version
                          ?? typeof(JellyfinClientIdentity).Assembly.GetName().Version;
            return version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
