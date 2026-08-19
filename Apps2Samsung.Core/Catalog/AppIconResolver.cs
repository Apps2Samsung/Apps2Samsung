using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Catalog
{
    public static class AppIconResolver
    {
        // Short timeout so a flaky network fails fast to the lettered-avatar fallback instead of
        // hanging on HttpClient's 100s default while the user waits on the Installed-apps list.
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static Dictionary<string, string>? _iconMapCache;
        private static bool _fetchAttempted;

        // Primary: our own mirror in the Apps2Samsung org, so the app doesn't depend on a third-party
        // personal domain. Fallback: the upstream community source it was mirrored from. (The icon
        // images themselves live on Samsung's CDN, referenced from inside this JSON.)
        private static readonly string[] JsonUrls =
        {
            "https://raw.githubusercontent.com/Apps2Samsung/tizen-community-packages/refs/heads/main/data/samsung-tv-app-ids.json",
            "https://rs.ltd/data/samsung-tv-app-ids.json",
        };

        /// <summary>
        /// Fetches and parses the community-maintained TV App IDs JSON file.
        /// Caches the result in memory for the lifetime of the application.
        /// Returns a dictionary mapping TizenId to Icon URL.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, string>> GetIconMapAsync()
        {
            if (_iconMapCache != null)
                return _iconMapCache;

            if (_fetchAttempted)
                return new Dictionary<string, string>(); // Return empty map if we previously failed to fetch

            _fetchAttempted = true;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Try each source in order; the first that returns a parseable manifest wins. A dead host
            // or DNS failure (e.g. the phone couldn't resolve the domain) just falls through to the next
            // one, and if all fail the UI shows lettered-avatar fallbacks.
            foreach (var url in JsonUrls)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var json = await response.Content.ReadAsStringAsync();
                    var manifest = JsonSerializer.Deserialize<TvAppIconManifest>(json, JsonSerializerOptionsProvider.Default);
                    if (manifest?.Items == null)
                        continue;

                    foreach (var item in manifest.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Icon))
                            continue;

                        // The ID field can be a comma-separated list of Tizen IDs
                        var ids = item.Id.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        foreach (var id in ids)
                        {
                            // Overwrite is fine if duplicates exist
                            map[id] = item.Icon;
                        }

                        // Also map by the human-readable app name for fallback resolution
                        if (!string.IsNullOrWhiteSpace(item.Name))
                        {
                            map[item.Name] = item.Icon;

                            // Also map the lower invariant for fuzzy matching
                            map[item.Name.ToLowerInvariant()] = item.Icon;
                        }
                    }

                    if (map.Count > 0)
                        break; // got a usable map — don't hit the fallback source
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"Failed to fetch AppIcon manifest from {url}: {ex.Message}");
                }
            }

            // Also map some built-in community apps as a fallback (using jellyfin defaults if needed)
            // The JSON from rs.ltd contains mostly official apps.
            if (!map.ContainsKey("k5Mv1gJ5iY.Jellyfin"))
            {
                // We don't have the ProviderManifest here easily, but we know Jellyfin's Tizen ID.
                // We'll leave unknown apps to be handled by the fallback avatar in the UI.
            }

            _iconMapCache = map;
            return _iconMapCache;
        }

        /// <summary>
        /// Attempts to get the icon URL for a given Tizen ID. Returns null if not found.
        /// </summary>
        public static async Task<string?> TryGetIconUrlAsync(string tizenId)
        {
            if (string.IsNullOrWhiteSpace(tizenId))
                return null;

            var map = await GetIconMapAsync();
            if (map.TryGetValue(tizenId, out var iconUrl))
                return iconUrl;

            // Optional: fallback checks for app titles if exact ID not matched.
            return null;
        }
    }
}
