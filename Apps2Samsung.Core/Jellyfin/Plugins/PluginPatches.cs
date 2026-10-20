using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    // EditorsChoice (the server's script, transpiled at install)
    // ------------------------
    public sealed class EditorsChoicePatch : IJellyfinPluginPatch
    {
        // The plugin's own banner, as the server serves it at install time, with the user's Editor's
        // Choice settings (the replacement banner this patch used to inject ignored them, and could
        // be added twice when the server answered slowly). The script is transpiled to ES2015 like
        // every other plugin script (the server's copy uses syntax older TVs cannot parse) and
        // carried inside the loader as a string; the loader fixes what breaks in the app, see its
        // comments. Checked on a Samsung TV (Tizen 9).
        private const string SourcePlaceholder = "/*EDITORS_CHOICE_SOURCE*/null";

        private const string Loader = @"(function () {
    // Editor's Choice, on every start. Its images use addresses relative to the web client
    // ('../Items/...'), which from the app's local page point nowhere: they are given the
    // server's address. Wrapped in a function so its top-level names stay its own.
    //
    // On a TV:
    // - the banner turns by itself, and its arrows were only a stop where the remote's focus got
    //   lost (Editor's Choice sends it back to the tabs from there): they are hidden;
    // - every slide stays in place for the fade, the hidden ones only transparent, so the remote
    //   landed on a hidden slide and OK opened another title than the one shown: only the shown
    //   slide stays reachable, and its button lights up when the banner has the focus (instead
    //   of the blue frame drawn around the whole banner);
    // - left and right on the banner show the previous or next title, the focus staying on it.
    var SOURCE = /*EDITORS_CHOICE_SOURCE*/null;
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

        var script = document.createElement('script');
        script.textContent = '(function () {\n'
            + SOURCE.split('../Items/').join(server + '/Items/')
            + '\n})();';
        document.head.appendChild(script);
    }
    load();
})();
";

        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var url = UrlHelper.CombineUrl(ctx.ServerUrl, "/EditorsChoice/script");
            var source = await ctx.PluginManager.DownloadStringAsync(url);
            if (string.IsNullOrWhiteSpace(source))
            {
                Trace.WriteLine("⚠ Editor's Choice script not downloaded, banner not injected.");
                return;
            }
            source = await EsbuildHelper.TranspileAsync(source, "EditorsChoice/script");
            if (source == null)
                return;

            // The default encoder escapes everything outside ASCII, U+2028/U+2029 included, which
            // older engines do not accept inside a string literal.
            var loader = Loader.Replace(SourcePlaceholder, JsonSerializer.Serialize(source));

            var dir = Path.Combine(ctx.PluginCacheDir, "editorschoice");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "loader.js"), loader, Encoding.UTF8);

            const string injectedSrc = "plugin_cache/editorschoice/loader.js";
            ctx.InjectScript($"<script src=\"{injectedSrc}\"></script>", injectedSrc);
        }
    }

    // ------------------------
    // Jellyfin Enhanced (the server's scripts, transpiled at install)
    // ------------------------
    public sealed class JellyfinEnhancedPatch : IJellyfinPluginPatch
    {
        // Jellyfin Enhanced adds its other scripts itself, at addresses it builds with
        // ApiClient.getUrl, and fetches its settings and translations the same way. The scripts are
        // copied into the app at install time and transpiled to ES2015 (the server's copies use
        // syntax older TVs cannot parse); everything else still comes from the server. Which scripts
        // depends on the version: from 12.10 the component modules come as one /bundle.js, before
        // that as separate files listed in the main script (`allComponentScripts`). A plugin update
        // reaches the TV with the next install.
        //
        // The script reads its version from its own tag (attributes `plugin` and `version`, as on
        // the server's page) and only uses it as a cache key in its URLs.
        private const string ServerRoot = "/JellyfinEnhanced/";
        private const string BundlePath = "bundle.js";

        private static readonly Regex ScriptLiteral = new(
            @"['""`]/JellyfinEnhanced/(js/[A-Za-z0-9_./-]+\.js)", RegexOptions.Compiled);

        private static readonly Regex ComponentList = new(
            @"allComponentScripts\s*=\s*\[(.*?)\]", RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex ComponentEntry = new(
            @"['""]([A-Za-z0-9_][A-Za-z0-9_./-]*\.js)['""]", RegexOptions.Compiled);

        // Points every script address under /JellyfinEnhanced/ at the copy in the app. A script the
        // app does not carry fails to load instead of arriving untranspiled.
        private const string Redirect = @"(function () {
    var ROOT = '/JellyfinEnhanced/';
    var LOCAL = 'plugin_cache/JellyfinEnhanced/';
    function local(url) {
        if (typeof url !== 'string' || !/^https?:/i.test(url)) {
            return url;
        }
        var at = url.indexOf(ROOT);
        if (at === -1) {
            return url;
        }
        var path = url.substring(at + ROOT.length).split('#')[0].split('?')[0];
        if (path === 'script') {
            path = 'script.js';
        }
        return /\.js$/i.test(path) ? LOCAL + path : url;
    }
    var src = Object.getOwnPropertyDescriptor(HTMLScriptElement.prototype, 'src');
    if (!src || !src.set) {
        return;
    }
    Object.defineProperty(HTMLScriptElement.prototype, 'src', {
        configurable: true,
        enumerable: src.enumerable,
        get: src.get,
        set: function (value) { src.set.call(this, local(value)); }
    });
})();
";

        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            var manager = ctx.PluginManager;
            string Url(string path) => UrlHelper.CombineUrl(ctx.ServerUrl, ServerRoot + path);

            var version = (await manager.DownloadStringAsync(Url("version")))?.Trim().Trim('"');
            var main = await manager.DownloadStringAsync(Url("script"));
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(main))
            {
                Trace.WriteLine("⚠ Jellyfin Enhanced version or main script not downloaded, not injected.");
                return;
            }

            var mainTranspiled = await EsbuildHelper.TranspileAsync(main, "JellyfinEnhanced/script");
            if (mainTranspiled == null)
                return;

            var dir = Path.Combine(ctx.PluginCacheDir, "JellyfinEnhanced");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "script.js"), mainTranspiled, Encoding.UTF8);

            var paths = new List<string>();
            foreach (Match m in ScriptLiteral.Matches(main))
                paths.Add(m.Groups[1].Value);

            var list = ComponentList.Match(main);
            if (list.Success)
            {
                foreach (Match m in ComponentEntry.Matches(list.Groups[1].Value))
                    paths.Add("js/" + m.Groups[1].Value);
            }
            else
            {
                paths.Add(BundlePath);
            }

            int saved = 0;
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var relPath = Path.Combine("JellyfinEnhanced", path.Replace('/', Path.DirectorySeparatorChar));
                if (await manager.DownloadAndTranspileAsync(Url(path), ctx.PluginCacheDir, relPath) != null)
                    saved++;
            }
            Trace.WriteLine($"   Jellyfin Enhanced {version}: {saved} of {paths.Count} scripts copied.");

            await File.WriteAllTextAsync(Path.Combine(dir, "redirect.js"), Redirect, Encoding.UTF8);
            const string redirectSrc = "plugin_cache/JellyfinEnhanced/redirect.js";
            ctx.InjectScript($"<script src=\"{redirectSrc}\"></script>", redirectSrc);

            const string mainSrc = "plugin_cache/JellyfinEnhanced/script.js";
            var versionAttr = WebUtility.HtmlEncode(version);
            ctx.InjectScript(
                $"<script plugin=\"Jellyfin Enhanced\" version=\"{versionAttr}\" dev=\"false\" src=\"{mainSrc}\"></script>",
                mainSrc);
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
                Trace.WriteLine("⚠ NotifySync client.js not downloaded, bell not injected.");
                return;
            }

            // Same placement File Transformation uses on the server: end of body, no defer.
            // The script itself waits for window.ApiClient and resolves the server address from it.
            const string src = "plugin_cache/NotifySync/client.js";
            ctx.InjectScript($"<script src=\"{src}\"></script>", src);
        }
    }

    // ------------------------
    // Media Carousel (single script served by the plugin's own controller)
    // ------------------------
    public sealed class MediaCarouselPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            // "/MediaCarousel/media-carousel.js" -> plugin_cache/MediaCarousel/media-carousel.js, transpiled on the way.
            await ctx.PluginManager.DownloadExplicitFilesAsync(ctx.ServerUrl, ctx.PluginCacheDir, ctx.MatrixEntry);

            var local = Path.Combine(ctx.PluginCacheDir, "MediaCarousel", "media-carousel.js");
            if (!File.Exists(local))
            {
                Trace.WriteLine("⚠ Media Carousel script not downloaded, rows not injected.");
                return;
            }

            // End of body, like the server's own tag. The script waits for a signed-in ApiClient
            // and builds every address with ApiClient.getUrl, so it reaches the server from the app.
            const string src = "plugin_cache/MediaCarousel/media-carousel.js";
            ctx.InjectScript($"<script src=\"{src}\"></script>", src);
        }
    }

    // ------------------------
    // Media Bar and Home Screen Sections (the server's client files, GitHub as fallback)
    // ------------------------

    /// <summary>
    /// Caches a plugin's client script and stylesheet from the plugin's own controller on the server,
    /// which serves the copy built into the installed plugin, so the script never calls endpoints that
    /// server doesn't have. GitHub main is only used when the server doesn't serve the file (older
    /// plugin versions).
    /// </summary>
    internal static class ServerPluginFiles
    {
        public static async Task CacheAsync(
            PluginPatchContext ctx,
            string dir,
            string scriptServerPath,
            string scriptFallbackUrl,
            string styleServerPath,
            string styleFallbackUrl)
        {
            var manager = ctx.PluginManager;
            Directory.CreateDirectory(Path.Combine(ctx.PluginCacheDir, dir));

            var styleName = Path.GetFileName(styleServerPath);
            var style = await manager.DownloadBytesAsync(UrlHelper.CombineUrl(ctx.ServerUrl, styleServerPath))
                ?? await manager.DownloadBytesAsync(styleFallbackUrl);
            if (style != null)
            {
                await File.WriteAllBytesAsync(Path.Combine(ctx.PluginCacheDir, dir, styleName), style);
                ctx.InjectStyle($"plugin_cache/{dir}/{styleName}");
            }

            var scriptName = Path.GetFileName(scriptServerPath);
            var relPath = Path.Combine(dir, scriptName);
            var local = await manager.DownloadAndTranspileAsync(UrlHelper.CombineUrl(ctx.ServerUrl, scriptServerPath), ctx.PluginCacheDir, relPath);
            if (local == null)
            {
                Trace.WriteLine($"⚠ {ctx.MatrixEntry.Name}: {scriptServerPath} not served, falling back to {scriptFallbackUrl}");
                local = await manager.DownloadAndTranspileAsync(scriptFallbackUrl, ctx.PluginCacheDir, relPath);
            }

            if (local != null)
            {
                var src = $"plugin_cache/{dir}/{scriptName}";
                ctx.InjectScript($"<script src=\"{src}\"></script>", src);
            }
        }
    }

    public sealed class MediaBarPatch : IJellyfinPluginPatch
    {
        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            // With File Transformation the server's index.html carries "../MediaBar/slideshowpure.js",
            // which the "mediabar" asset rule has already cached and injected.
            if (ctx.InjectedScripts.Contains("plugin_cache/slideshowpure.js"))
            {
                Trace.WriteLine("ℹ Media Bar: already cached from the server's index.html.");
                return;
            }

            await ServerPluginFiles.CacheAsync(
                ctx,
                "mediabar",
                "/MediaBar/slideshowpure.js",
                "https://cdn.jsdelivr.net/gh/IAmParadox27/jellyfin-plugin-media-bar@main/slideshowpure.js",
                "/MediaBar/slideshowpure.css",
                "https://cdn.jsdelivr.net/gh/IAmParadox27/jellyfin-plugin-media-bar@main/slideshowpure.css");
        }
    }

    public sealed class HomeScreenSectionsPatch : IJellyfinPluginPatch
    {
        private const string GitHubInject =
            "https://raw.githubusercontent.com/IAmParadox27/jellyfin-plugin-home-sections/main/src/Jellyfin.Plugin.HomeScreenSections/Inject/";

        public Task ApplyAsync(PluginPatchContext ctx)
            => ServerPluginFiles.CacheAsync(
                ctx,
                "homescreensections",
                "/HomeScreen/home-screen-sections.js",
                GitHubInject + "HomeScreenSections.js",
                "/HomeScreen/home-screen-sections.css",
                GitHubInject + "HomeScreenSections.css");
    }
}
