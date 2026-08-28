using Apps2Samsung.Helpers;
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
