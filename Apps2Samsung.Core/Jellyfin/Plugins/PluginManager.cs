using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin.Plugins.KefinTweaks;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins
{
    /// <summary>
    /// Responsible for:
    /// - Mapping installed plugins -> PluginMatrixEntry
    /// - Resolving plugin patch implementations (per-plugin behavior)
    /// - Download/transpile helpers used by patches
    /// </summary>
    public sealed class PluginManager
    {
        private readonly HttpClient _httpClient;
        private readonly JellyfinApiClient _apiClient;

        public PluginManager(HttpClient httpClient, JellyfinApiClient apiClient)
        {
            _httpClient = httpClient;
            _apiClient = apiClient;
        }

        // Single source of truth for which matrix entries have a transpilable patch implementation.
        // Used by ResolvePatch (instance) and IsSupported (static, called from the config UI).
        private static readonly Dictionary<string, Func<IJellyfinPluginPatch>> PatchFactories =
            new(StringComparer.Ordinal)
            {
                ["EditorsChoice"]        = () => new EditorsChoicePatch(),
                ["Jellyfin Enhanced"]    = () => new JellyfinEnhancedPatch(),
                ["Media Bar"]            = () => new MediaBarPatch(),
                ["Home Screen Sections"] = () => new HomeScreenSectionsPatch(),
                ["KefinTweaks"]          = () => new KefinTweaksPatch(),
            };

        public PluginMatrixEntry? FindPluginEntry(JellyfinPluginInfo? plugin)
            => FindMatrixEntry(plugin);

        public IJellyfinPluginPatch? ResolvePatch(PluginMatrixEntry entry)
            => PatchFactories.TryGetValue(entry.Name, out var factory) ? factory() : null;

        public static PluginMatrixEntry? FindMatrixEntry(JellyfinPluginInfo? plugin)
        {
            if (plugin?.Name == null) return null;

            string pluginName = plugin.Name.ToLowerInvariant();

            return PluginMatrix.Matrix.FirstOrDefault(entry =>
                pluginName.Contains(entry.Name, StringComparison.InvariantCultureIgnoreCase));
        }

        public static bool IsSupported(JellyfinPluginInfo? plugin)
        {
            var entry = FindMatrixEntry(plugin);
            return entry != null && PatchFactories.ContainsKey(entry.Name);
        }

        public bool TryClassifyServerAsset(string url, out ServerAssetKind kind)
        {
            kind = ServerAssetKind.Unknown;

            foreach (var rule in PluginMatrix.ServerAssetRules)
            {
                if (rule.match(url))
                {
                    kind = rule.treatAs;
                    return true;
                }
            }

            return false;
        }

        public async Task DownloadExplicitFilesAsync(string serverUrl, string pluginCacheDir, PluginMatrixEntry entry)
        {
            if (entry?.ExplicitServerFiles == null || entry.ExplicitServerFiles.Count == 0)
                return;

            Trace.WriteLine("▶ Downloading explicit Enhanced JS modules...");

            foreach (var rel in entry.ExplicitServerFiles)
            {
                try
                {
                    string url = UrlHelper.CombineUrl(serverUrl, rel);
                    Trace.WriteLine($"   → Fetch: {url}");

                    string js = await _httpClient.GetStringAsync(url);

                    // Transpile to es2015 using esbuild; fallback is original JS.
                    js = await EsbuildHelper.TranspileAsync(js, rel);

                    string relPath = rel.TrimStart('/');
                    string outPath = Path.Combine(pluginCacheDir, relPath.Replace('/', Path.DirectorySeparatorChar));

                    string? directory = Path.GetDirectoryName(outPath);
                    if (directory != null)
                        Directory.CreateDirectory(directory);

                    if (!Path.HasExtension(outPath) || !outPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                        outPath += ".js";

                    await File.WriteAllTextAsync(outPath, js, Encoding.UTF8);

                    string logPath = outPath.Replace(pluginCacheDir + Path.DirectorySeparatorChar, "plugin_cache/");
                    Trace.WriteLine($"      ✓ Saved {logPath}");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"      ⚠ Failed Enhanced JS '{rel}': {ex}");
                }
            }
        }
        public async Task<string?> DownloadAndTranspileAsync(string url, string cacheDir, string relPath)
        {
            try
            {
                Trace.WriteLine($"▶ Downloading plugin JS: {url}");

                string js = await _httpClient.GetStringAsync(url);
                js = await EsbuildHelper.TranspileAsync(js, relPath);

                string localPath = Path.Combine(cacheDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                await File.WriteAllTextAsync(localPath, js, Encoding.UTF8);
                return localPath;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ Plugin download failed: {ex}");
                return null;
            }
        }

        public async Task<byte[]?> DownloadBytesAsync(string url)
        {
            try
            {
                return await _httpClient.GetByteArrayAsync(url);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ DownloadBytes download failed: {url} {ex}");
                return null;
            }
        }

        public async Task<string?> DownloadStringAsync(string url)
        {
            try
            {
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ DownloadString failed: {url} {ex}");
                return null;
            }
        }

        public Task<JellyfinPublicSystemInfo?> GetPublicSystemInfoAsync(string serverUrl)
            => _apiClient.GetPublicSystemInfoAsync(serverUrl);

        public JellyfinApiClient Api => _apiClient;
    }
}