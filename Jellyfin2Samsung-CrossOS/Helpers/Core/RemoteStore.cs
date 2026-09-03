using Apps2Samsung.Helpers;
using Apps2Samsung.Remote;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Apps2Samsung.Helpers.Core
{
    /// <summary>
    /// Remembers, per TV, the two things the remote can't rediscover on its own: the pairing token
    /// (without it the TV re-prompts every session) and the MAC captured while the set was awake
    /// (without it a sleeping set can't be woken). Stored as JSON maps keyed by IP in settings.json,
    /// the same shape the custom-icon/title maps use. The mobile head keeps the equivalent in its
    /// Preferences store.
    /// </summary>
    public static class RemoteStore
    {
        public static string? GetToken(string tvIpAddress) =>
            Read(AppSettings.Default.RemoteTokensJson).GetValueOrDefault(tvIpAddress);

        public static void SetToken(string tvIpAddress, string token) =>
            AppSettings.Default.RemoteTokensJson = Write(AppSettings.Default.RemoteTokensJson, tvIpAddress, token);

        public static string? GetMac(string tvIpAddress) =>
            Read(AppSettings.Default.RemoteMacsJson).GetValueOrDefault(tvIpAddress);

        public static void SetMac(string tvIpAddress, string macAddress) =>
            AppSettings.Default.RemoteMacsJson = Write(AppSettings.Default.RemoteMacsJson, tvIpAddress, macAddress);

        /// <summary>
        /// The same store behind Core's <see cref="IRemoteCredentialStore"/>, so
        /// <see cref="RemoteSession"/> can pair and wake on this head's behalf without knowing where
        /// the values live.
        /// </summary>
        public sealed class Credentials : IRemoteCredentialStore
        {
            public static readonly Credentials Instance = new();

            public string? GetToken(string tvIpAddress) => RemoteStore.GetToken(tvIpAddress);
            public void SetToken(string tvIpAddress, string token) => RemoteStore.SetToken(tvIpAddress, token);
            public string? GetMac(string tvIpAddress) => RemoteStore.GetMac(tvIpAddress);
            public void SetMac(string tvIpAddress, string macAddress) => RemoteStore.SetMac(tvIpAddress, macAddress);
        }

        /// <summary>
        /// What this head says when a connection attempt ends short of an open channel. The two heads
        /// word the "never seen this TV awake" case differently, so the key is picked here rather than
        /// in Core.
        /// </summary>
        public static string StatusKeyFor(RemoteSessionOutcome outcome) => outcome switch
        {
            RemoteSessionOutcome.NoMacToWake => "lblRemoteWakeNoMac",
            RemoteSessionOutcome.WakeFailed => "lblRemoteWakeFailed",
            RemoteSessionOutcome.PairingRefused => "lblRemotePairFailed",
            _ => "lblRemoteNoChannel",
        };

        private static Dictionary<string, string> Read(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return new Dictionary<string, string>(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // A hand-edited or truncated settings.json shouldn't break the remote — start over.
                Trace.WriteLine($"[remote] could not read the stored map: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static string Write(string json, string key, string value)
        {
            var map = Read(json);
            map[key] = value;
            var updated = JsonSerializer.Serialize(map);
            AppSettings.Default.Save();
            return updated;
        }
    }
}
