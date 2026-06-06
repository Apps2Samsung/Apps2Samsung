using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Helpers.Jellyfin.Plugins.EditorsChoice;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins
{
    public sealed class PluginPatchContext
    {
        public PluginPatchContext(
            PackageWorkspace workspace,
            string serverUrl,
            string pluginCacheDir,
            PluginMatrixEntry matrixEntry,
            PluginManager pluginManager,
            StringBuilder cssBuilder,
            StringBuilder headJsBuilder,
            StringBuilder bodyJsBuilder,
            System.Collections.Generic.HashSet<string> injectedScripts,
            System.Collections.Generic.HashSet<string> injectedStyles)
        {
            Workspace = workspace;
            ServerUrl = serverUrl;
            PluginCacheDir = pluginCacheDir;
            MatrixEntry = matrixEntry;
            PluginManager = pluginManager;
            CssBuilder = cssBuilder;
            HeadJsBuilder = headJsBuilder;
            BodyJsBuilder = bodyJsBuilder;
            InjectedScripts = injectedScripts;
            InjectedStyles = injectedStyles;
        }

        public PackageWorkspace Workspace { get; }
        public string ServerUrl { get; }
        public string PluginCacheDir { get; }
        public PluginMatrixEntry MatrixEntry { get; }
        public PluginManager PluginManager { get; }

        public StringBuilder CssBuilder { get; }
        public StringBuilder HeadJsBuilder { get; }
        public StringBuilder BodyJsBuilder { get; }

        // For dedup / consistent injection
        public System.Collections.Generic.HashSet<string> InjectedScripts { get; }
        public System.Collections.Generic.HashSet<string> InjectedStyles { get; }

        public void InjectScript(string scriptTag, string src)
        {
            if (InjectedScripts.Add(src))
                BodyJsBuilder.AppendLine(scriptTag);
        }

        public void InjectStyle(string href)
        {
            if (InjectedStyles.Add(href))
                CssBuilder.AppendLine($"<link rel=\"stylesheet\" href=\"{href}\" />");
        }
    }

    // ------------------------
    // EditorsChoice
    // ------------------------
    public sealed class EditorsChoicePatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var entry = ctx.MatrixEntry;

            foreach (var url in entry.FallbackUrls)
            {
                string cleanName = RegexPatterns.PluginName.NonAlphanumeric.Replace(entry.Name.ToLowerInvariant(), "");
                string fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                string relPath = Path.Combine(cleanName, fileName);

                var local = await ctx.PluginManager.DownloadAndTranspileAsync(url, ctx.PluginCacheDir, relPath);
                if (local == null) continue;

                // Apply EditorsChoice patch (your existing patch class)
                var js = await File.ReadAllTextAsync(local, Encoding.UTF8);
                js = new PatchEditorsChoice().ApplyAsync(js);
                await File.WriteAllTextAsync(local, js, Encoding.UTF8);

                string injectedSrc = $"plugin_cache/{cleanName}/{fileName}";
                ctx.InjectScript($"<script defer src=\"{injectedSrc}\"></script>", injectedSrc);
                break;
            }
        }
    }

    // ------------------------
    // Jellyfin Enhanced (ExplicitServerFiles)
    // ------------------------
    public sealed class JellyfinEnhancedPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var entry = ctx.MatrixEntry;

            // Download all explicit server files into plugin_cache preserving structure
            await ctx.PluginManager.DownloadExplicitFilesAsync(ctx.ServerUrl, ctx.PluginCacheDir, entry);

            // Inject main script last
            // Your explicit list contains "/JellyfinEnhanced/script" -> stored as plugin_cache/JellyfinEnhanced/script.js
            var main = Path.Combine(ctx.PluginCacheDir, "JellyfinEnhanced", "script.js");
            if (File.Exists(main))
            {
                await PatchEnhancedMainScript(main);

                var outSrc = "plugin_cache/JellyfinEnhanced/script.js";
                ctx.InjectScript($"<script src=\"{outSrc}\"></script>", outSrc);
            }
        }

        private static async Task PatchEnhancedMainScript(string scriptPath)
        {
            string original = await File.ReadAllTextAsync(scriptPath);

            string patch = @"
// ---- J2S SCRIPT PATCH: FORCE LOCAL ENHANCED MODULE LOADING ----
(function () {

    function rewriteEnhancedUrl(url) {
        try {
            if (typeof url !== 'string') return url;

            var base = url.split('?')[0];

            if (base.endsWith('.js') && base.indexOf('/JellyfinEnhanced/') !== -1) {
                var idx = base.indexOf('/JellyfinEnhanced/');
                var sub = base.substring(idx + '/JellyfinEnhanced/'.length);
                return 'plugin_cache/JellyfinEnhanced/' + sub;
            }

            return url;
        }
        catch (e) {
            console.error('J2S rewriteEnhancedUrl failed', e);
            return url;
        }
    }

    var _createElement = document.createElement;
    document.createElement = function (tag) {
        var el = _createElement.call(document, tag);

        if (tag && tag.toLowerCase() === 'script') {
            var _setAttribute = el.setAttribute;

            el.setAttribute = function (name, value) {
                if (name === 'src') {
                    value = rewriteEnhancedUrl(value);
                }
                return _setAttribute.call(el, name, value);
            };

            Object.defineProperty(el, 'src', {
                configurable: true,
                get: function () { return el.getAttribute('src'); },
                set: function (value) {
                    value = rewriteEnhancedUrl(value);
                    _setAttribute.call(el, 'src', value);
                }
            });
        }

        return el;
    };

    if (typeof window.fetch === 'function') {
        var _fetch = window.fetch;
        window.fetch = function (resource, init) {
            try {
                if (typeof resource === 'string') resource = rewriteEnhancedUrl(resource);
            } catch (e) {
                console.error('FETCH rewrite failed', e);
            }
            return _fetch.call(this, resource, init);
        };
    }

    console.log('🪼 J2S: Enhanced loader patched to use plugin_cache for Enhanced JS modules');
})();
";
            await File.WriteAllTextAsync(scriptPath, patch + "\n\n" + original);
        }
    }

    // ------------------------
    // Media Bar (example: CSS + fallback JS)
    // ------------------------
    public sealed class MediaBarPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            // CSS
            const string cssUrl = "https://cdn.jsdelivr.net/gh/IAmParadox27/jellyfin-plugin-media-bar@main/slideshowpure.css";
            var bytes = await ctx.PluginManager.DownloadBytesAsync(cssUrl);
            if (bytes != null)
            {
                var dir = Path.Combine(ctx.PluginCacheDir, "mediabar");
                Directory.CreateDirectory(dir);

                var local = Path.Combine(dir, "slideshowpure.css");
                await File.WriteAllBytesAsync(local, bytes);

                ctx.InjectStyle("plugin_cache/mediabar/slideshowpure.css");
            }

            // JS (from matrix fallback)
            var entry = ctx.MatrixEntry;
            foreach (var url in entry.FallbackUrls)
            {
                var clean = "mediabar";
                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                var relPath = Path.Combine(clean, fileName);

                var local = await ctx.PluginManager.DownloadAndTranspileAsync(url, ctx.PluginCacheDir, relPath);
                if (local == null) continue;

                var injectedSrc = $"plugin_cache/{clean}/{fileName}";
                ctx.InjectScript($"<script src=\"{injectedSrc}\"></script>", injectedSrc);
                break;
            }
        }
    }

    // ------------------------
    // Home Screen Sections (CSS + fallback JS)
    // ------------------------
    public sealed class HomeScreenSectionsPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            const string cssUrl =
                "https://raw.githubusercontent.com/IAmParadox27/jellyfin-plugin-home-sections/main/src/Jellyfin.Plugin.HomeScreenSections/Inject/HomeScreenSections.css";

            var bytes = await ctx.PluginManager.DownloadBytesAsync(cssUrl);
            if (bytes != null)
            {
                var dir = Path.Combine(ctx.PluginCacheDir, "homescreensections");
                Directory.CreateDirectory(dir);

                var local = Path.Combine(dir, "HomeScreenSections.css");
                await File.WriteAllBytesAsync(local, bytes);

                ctx.InjectStyle("plugin_cache/homescreensections/HomeScreenSections.css");
            }

            var entry = ctx.MatrixEntry;
            foreach (var url in entry.FallbackUrls)
            {
                var clean = "homescreensections";
                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                var relPath = Path.Combine(clean, fileName);

                var local = await ctx.PluginManager.DownloadAndTranspileAsync(url, ctx.PluginCacheDir, relPath);
                if (local == null) continue;

                var injectedSrc = $"plugin_cache/{clean}/{fileName}";
                ctx.InjectScript($"<script src=\"{injectedSrc}\"></script>", injectedSrc);
                break;
            }
        }
    }
}
