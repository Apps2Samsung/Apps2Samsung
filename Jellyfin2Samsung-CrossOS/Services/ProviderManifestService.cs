using Avalonia;
using Avalonia.Platform;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    public class ProviderManifestService
    {
        private const string RemoteUrl =
            "https://raw.githubusercontent.com/Apps2Samsung/Apps2Samsung/main/third-party-apps.json";

        private static readonly Uri BundledUri =
            new("avares://Apps2Samsung/Assets/third-party-apps.json");

        // The remote-manifest cache. On macOS this must NOT live next to the binary: writing it into
        // the .app bundle mutates it and breaks the code signature / TCC Local Network prompt (#498).
        private static readonly string CachePath = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Caches", "Apps2Samsung", "third-party-apps.cache.json")
            : Path.Combine(AppSettings.FolderPath, "third-party-apps.cache.json");

        private readonly HttpClient _httpClient;

        public ProviderManifestService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<ProviderManifest> GetAsync()
        {
            // 1. Try remote (and refresh the cache on success).
            try
            {
                using var response = await _httpClient.GetAsync(RemoteUrl);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var manifest = Deserialize(json);
                if (manifest != null)
                {
                    TryWriteCache(json);
                    return manifest;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ProviderManifest: remote fetch failed: {ex.Message}");
            }

            // 2. Try local cache (last known good).
            try
            {
                if (File.Exists(CachePath))
                {
                    var json = await File.ReadAllTextAsync(CachePath);
                    var manifest = Deserialize(json);
                    if (manifest != null)
                        return manifest;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ProviderManifest: cache read failed: {ex.Message}");
            }

            // 3. Bundled fallback.
            try
            {
                using var stream = AssetLoader.Open(BundledUri);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                var manifest = Deserialize(json);
                if (manifest != null)
                    return manifest;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ProviderManifest: bundled fallback failed: {ex.Message}");
            }

            return new ProviderManifest();
        }

        private static ProviderManifest? Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<ProviderManifest>(
                    json,
                    JsonSerializerOptionsProvider.Default);
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"ProviderManifest: parse failed: {ex.Message}");
                return null;
            }
        }

        private static void TryWriteCache(string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, json);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"ProviderManifest: cache write failed: {ex.Message}");
            }
        }
    }
}
