using Apps2Samsung.Helpers.API;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins
{
    /// <summary>
    /// Orchestrates plugin-related patching:
    /// - Fetch server index
    /// - Cache plugin assets referenced by index.html
    /// - Apply API-installed plugins using PluginManager + plugin patch classes
    /// </summary>
    public class JellyfinPluginPatcher
    {
        private readonly HttpClient _httpClient;
        private readonly JellyfinApiClient _apiClient;
        private readonly PluginManager _pluginManager;

        private readonly HashSet<string> _injectedScripts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _injectedStyles = new(StringComparer.OrdinalIgnoreCase);

        public JellyfinPluginPatcher(
            HttpClient httpClient,
            JellyfinApiClient apiClient,
            PluginManager pluginManager)
        {
            _httpClient = httpClient;
            _apiClient = apiClient;
            _pluginManager = pluginManager;
        }

        public async Task PatchPluginsAsync(
            PackageWorkspace workspace,
            string serverUrl,
            StringBuilder cssBuilder,
            StringBuilder headJsBuilder,
            StringBuilder bodyJsBuilder)
        {
            string pluginCacheDir = Path.Combine(workspace.Root, "www", "plugin_cache");
            Directory.CreateDirectory(pluginCacheDir);

            string serverHtml = await FetchServerIndexAsync(serverUrl);
            if (!string.IsNullOrWhiteSpace(serverHtml))
            {
                await CacheServerAssetsAsync(
                    serverHtml,
                    serverUrl,
                    pluginCacheDir,
                    cssBuilder,
                    bodyJsBuilder);
            }

            await ApplyApiInstalledPluginsAsync(
                workspace,
                serverUrl,
                pluginCacheDir,
                cssBuilder,
                headJsBuilder,
                bodyJsBuilder);
        }

        private async Task<string> FetchServerIndexAsync(string serverUrl)
        {
            try
            {
                var url = UrlHelper.CombineUrl(serverUrl, "/web/index.html");
                Trace.WriteLine($"▶ Fetching server index.html: {url}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                return await _httpClient.GetStringAsync(url, cts.Token);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ Failed to fetch server index.html: {ex}");
                return string.Empty;
            }
        }

        private async Task CacheServerAssetsAsync(
            string serverHtml,
            string serverUrl,
            string pluginCacheDir,
            StringBuilder cssBuilder,
            StringBuilder jsBuilder)
        {
            Trace.WriteLine("▶ Extracting plugin assets from server index…");

            // --- CSS ---
            var cssMatches = RegexPatterns.Html.LinkHref.Matches(serverHtml);

            foreach (Match m in cssMatches)
            {
                string href = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(href)) continue;

                if (!_pluginManager.TryClassifyServerAsset(href, out var kind) || kind != ServerAssetKind.PluginAsset)
                    continue;

                await CacheCssAsync(serverUrl, href, pluginCacheDir, cssBuilder);
            }

            // --- JS ---
            var jsMatches = RegexPatterns.Html.ScriptSrc.Matches(serverHtml);

            foreach (Match m in jsMatches)
            {
                string src = m.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(src)) continue;

                if (!_pluginManager.TryClassifyServerAsset(src, out var kind) || kind != ServerAssetKind.PluginAsset)
                    continue;

                await CacheJsAsync(serverUrl, src, pluginCacheDir, jsBuilder);
            }
        }

        private async Task CacheCssAsync(string serverUrl, string href, string pluginCacheDir, StringBuilder cssBuilder)
        {
            try
            {
                var uri = GetAbsoluteUri(serverUrl, href);
                var fileName = Path.GetFileName(uri.AbsolutePath);

                if (!fileName.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    return;

                var localPath = Path.Combine(pluginCacheDir, fileName);
                var bytes = await _httpClient.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(localPath, bytes);

                var outHref = $"plugin_cache/{fileName}";
                AppendStyleOnce(cssBuilder, outHref);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ Failed to cache CSS '{href}': {ex}");
            }
        }
        private async Task CacheJsAsync(string serverUrl, string src, string pluginCacheDir, StringBuilder jsBuilder)
        {
            try
            {
                // Server-relative or absolute; cache into plugin_cache root with filename
                var uri = GetAbsoluteUri(serverUrl, src);
                var fileName = Path.GetFileName(uri.AbsolutePath);

                if (!fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                    fileName += ".js";

                var localPath = Path.Combine(pluginCacheDir, fileName);

                var jsContent = await _httpClient.GetStringAsync(uri);
                jsContent = await EsbuildHelper.TranspileAsync(jsContent, uri.ToString());

                await File.WriteAllTextAsync(localPath, jsContent);

                var outSrc = $"plugin_cache/{fileName}";
                AppendScriptOnce(jsBuilder, $"<script src=\"{outSrc}\"></script>", outSrc);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"⚠ Failed to cache JS '{src}': {ex}");
            }
        }
        private async Task ApplyApiInstalledPluginsAsync(
            PackageWorkspace workspace,
            string serverUrl,
            string pluginCacheDir,
            StringBuilder cssBuilder,
            StringBuilder headJsBuilder,
            StringBuilder bodyJsBuilder)
        {
            var apiPlugins = await _apiClient.GetInstalledPluginsAsync(serverUrl);

            var disabledIds = (AppSettings.Default.DisabledPluginIds ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var plugin in apiPlugins)
            {
                if (!string.IsNullOrEmpty(plugin?.Id) && disabledIds.Contains(plugin.Id))
                {
                    Trace.WriteLine($"⏭ Plugin '{plugin.Name}' opted out by user, skipping.");
                    continue;
                }

                var entry = _pluginManager.FindPluginEntry(plugin);
                if (entry == null) continue;

                var patch = _pluginManager.ResolvePatch(entry);
                if (patch == null)
                {
                    Trace.WriteLine($"ℹ No patch implementation registered for plugin '{entry.Name}', skipping.");
                    continue;
                }

                Trace.WriteLine($"⚙ Applying plugin patch: {entry.Name}");

                var ctx = new PluginPatchContext(
                    workspace: workspace,
                    serverUrl: serverUrl,
                    pluginCacheDir: pluginCacheDir,
                    matrixEntry: entry,
                    pluginManager: _pluginManager,
                    cssBuilder: cssBuilder,
                    headJsBuilder: headJsBuilder,
                    bodyJsBuilder: bodyJsBuilder,
                    injectedScripts: _injectedScripts,
                    injectedStyles: _injectedStyles
                );

                await patch.ApplyAsync(ctx);
            }
        }
        private static Uri GetAbsoluteUri(string serverUrl, string relOrAbs)
        {
            return UrlHelper.GetAbsoluteUri(serverUrl, relOrAbs);
        }
        private void AppendScriptOnce(StringBuilder js, string scriptTag, string src)
        {
            if (_injectedScripts.Add(src))
                js.AppendLine(scriptTag);
            else
                Trace.WriteLine($"ℹ Script already injected, skipping: {src}");
        }
        private void AppendStyleOnce(StringBuilder css, string href)
        {
            if (_injectedStyles.Add(href))
                css.AppendLine($"<link rel=\"stylesheet\" href=\"{href}\" />");
            else
                Trace.WriteLine($"ℹ CSS already injected, skipping: {href}");
        }
    }
}