using Apps2Samsung.Helpers.Core;
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
    // EditorsChoice (loaded from the server)
    // ------------------------
    public sealed class EditorsChoicePatch : IJellyfinPluginPatch
    {
        // The plugin's own banner, from the server, with the user's Editor's Choice settings (the
        // replacement banner this patch used to inject ignored them, and could be added twice when
        // the server answered slowly). The loader fixes what breaks in the app; see its comments.
        // Checked on a Samsung TV (Tizen 9).
        private const string Loader = @"(function () {
    // Editor's Choice from the server, on every start. Its images use addresses relative to the
    // web client ('../Items/...'), which from the app's local page point nowhere: they are given
    // the server's address. Wrapped in a function so its top-level names stay its own.
    //
    // On a TV:
    // - the banner turns by itself, and its arrows were only a stop where the remote's focus got
    //   lost (Editor's Choice sends it back to the tabs from there): they are hidden;
    // - every slide stays in place for the fade, the hidden ones only transparent, so the remote
    //   landed on a hidden slide and OK opened another title than the one shown: only the shown
    //   slide stays reachable, and its button lights up when the banner has the focus (instead
    //   of the blue frame drawn around the whole banner);
    // - left and right on the banner show the previous or next title, the focus staying on it.
    var CSS = '.layout-tv .editorsChoiceContainer .emby-scrollbuttons{display:none!important;}'
        + '.layout-tv .editorsChoiceItemBanner:focus{outline:none!important;box-shadow:none!important;}'
        + '.layout-tv .editorsChoiceItemBanner:focus .editorsChoiceItemButton'
        + '{background:#fff!important;color:#000!important;transform:scale(1.08);}';

    function syncSlides() {
        if (!document.documentElement.classList.contains('layout-tv')) {
            return;
        }
        var slides = document.querySelectorAll('.editorsChoiceItemBanner');
        for (var i = 0; i < slides.length; i++) {
            var hidden = slides[i].getAttribute('aria-hidden') === 'true';
            if (hidden && slides[i].getAttribute('tabindex') !== '-1') {
                slides[i].setAttribute('tabindex', '-1');
            } else if (!hidden && slides[i].getAttribute('tabindex') === '-1') {
                slides[i].removeAttribute('tabindex');
            }
            var buttons = slides[i].querySelectorAll('button');
            for (var j = 0; j < buttons.length; j++) {
                if (buttons[j].getAttribute('tabindex') !== '-1') {
                    buttons[j].setAttribute('tabindex', '-1');
                }
            }
        }
    }

    function onKey(event) {
        if ((event.keyCode !== 37 && event.keyCode !== 39)
            || !document.documentElement.classList.contains('layout-tv')) {
            return;
        }
        var slide = event.target && event.target.closest ? event.target.closest('.editorsChoiceItemBanner') : null;
        var root = slide ? slide.closest('.splide') : null;
        var button = root
            ? root.querySelector(event.keyCode === 39 ? '.splide__arrow--next' : '.splide__arrow--prev')
            : null;
        if (!button) {
            return;
        }
        // Before Jellyfin, which would move the focus off the banner.
        event.preventDefault();
        event.stopImmediatePropagation();
        button.click();
        setTimeout(function () {
            syncSlides();
            var shown = root.querySelector('.editorsChoiceItemBanner.is-active');
            if (shown) {
                try { shown.focus({ preventScroll: true }); } catch (e) { shown.focus(); }
            }
        }, 0);
    }

    function load() {
        var client = window.ApiClient;
        var server = client && client.serverAddress ? client.serverAddress() : null;
        if (!server) {
            setTimeout(load, 250);
            return;
        }
        server = server.replace(/\/+$/, '');

        var style = document.createElement('style');
        style.textContent = CSS;
        document.head.appendChild(style);
        window.addEventListener('keydown', onKey, true);
        new MutationObserver(syncSlides).observe(document.body, {
            subtree: true, childList: true, attributes: true, attributeFilter: ['aria-hidden']
        });

        var request = new XMLHttpRequest();
        request.open('GET', server + '/EditorsChoice/script');
        request.onload = function () {
            if (request.status !== 200) {
                return;
            }
            var script = document.createElement('script');
            script.textContent = '(function () {\n'
                + request.responseText.split('../Items/').join(server + '/Items/')
                + '\n})();';
            document.head.appendChild(script);
        };
        request.send();
    }
    load();
})();
";

        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var dir = Path.Combine(ctx.PluginCacheDir, "editorschoice");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "loader.js"), Loader, Encoding.UTF8);

            const string injectedSrc = "plugin_cache/editorschoice/loader.js";
            ctx.InjectScript($"<script src=\"{injectedSrc}\"></script>", injectedSrc);
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
