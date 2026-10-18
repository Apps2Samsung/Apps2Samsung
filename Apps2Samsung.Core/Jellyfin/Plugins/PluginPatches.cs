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
    // Jellyfin Enhanced (loaded from the server)
    // ------------------------
    public sealed class JellyfinEnhancedPatch : IJellyfinPluginPatch
    {
        // Jellyfin Enhanced builds every URL with ApiClient.getUrl, so from the app it reaches the
        // server by itself: its main script, its module bundle, its translations. The app only keeps
        // a small loader that adds the main script on every start, so a plugin update reaches the TV
        // without rebuilding the app. (Copying a fixed list of its files no longer works: from 12.x
        // most of them moved, and 151 of the 159 scripts it asked for were missing from the app.)
        //
        // The server caches /JellyfinEnhanced/script for a year: the loader asks for the version
        // first and puts it in the URL. The script reads that version from its own tag (attributes
        // `plugin` and `version`, as on the server's page) to key the URLs of its modules.
        private const string Loader = @"(function () {
    function add(server, version) {
        var script = document.createElement('script');
        script.setAttribute('plugin', 'Jellyfin Enhanced');
        script.setAttribute('version', version);
        script.setAttribute('dev', 'false');
        script.src = server + '/JellyfinEnhanced/script?v=' + encodeURIComponent(version);
        document.head.appendChild(script);
    }
    function load() {
        var client = window.ApiClient;
        var server = client && client.serverAddress ? client.serverAddress() : null;
        if (!server) {
            setTimeout(load, 250);
            return;
        }
        server = server.replace(/\/+$/, '');
        var request = new XMLHttpRequest();
        request.open('GET', server + '/JellyfinEnhanced/version?_=' + Date.now());
        request.onload = function () {
            var version = request.status === 200 ? request.responseText.replace(/[""\s]/g, '') : '';
            add(server, version || String(Date.now()));
        };
        request.onerror = function () { add(server, String(Date.now())); };
        request.send();
    }
    load();
})();
";

        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var dir = Path.Combine(ctx.PluginCacheDir, "JellyfinEnhanced");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "loader.js"), Loader, Encoding.UTF8);

            const string injectedSrc = "plugin_cache/JellyfinEnhanced/loader.js";
            ctx.InjectScript($"<script src=\"{injectedSrc}\"></script>", injectedSrc);
        }
    }

    // ------------------------
    // NotifySync (single script served by the plugin's own controller)
    // ------------------------
    public sealed class NotifySyncPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            // "/NotifySync/client.js" -> plugin_cache/NotifySync/client.js, transpiled on the way.
            await ctx.PluginManager.DownloadExplicitFilesAsync(ctx.ServerUrl, ctx.PluginCacheDir, ctx.MatrixEntry);

            var local = Path.Combine(ctx.PluginCacheDir, "NotifySync", "client.js");
            if (!File.Exists(local))
            {
                System.Diagnostics.Trace.WriteLine("⚠ NotifySync client.js not downloaded, bell not injected.");
                return;
            }

            // Same placement File Transformation uses on the server: end of body, no defer.
            // The script itself waits for window.ApiClient and resolves the server address from it.
            const string src = "plugin_cache/NotifySync/client.js";
            ctx.InjectScript($"<script src=\"{src}\"></script>", src);
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
