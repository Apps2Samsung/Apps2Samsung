using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins.KefinTweaks
{
    /// <summary>
    /// KefinTweaks is not a server plugin but a JavaScript Injector user script: a bootstrap in
    /// public.js loads its installer from a root the admin picks (a jsDelivr tag, @latest, @main or a
    /// self-hosted folder), and the installer and injector load every module from that root. From 0.5
    /// on the KefinTweaks-Config script also carries the commit that root resolved to, which the loader
    /// prefers, and a baked KefinTweaks-injector entry that adds the module tags itself.
    ///
    /// Every script under the root the server's web clients use is downloaded at install, transpiled to
    /// ES2015 and stored in plugin_cache/kefinTweaks/ under the same relative path. A redirect loaded
    /// before public.js points each script element under any KefinTweaks root at that copy. The config
    /// is left as the server serves it: stylesheets, images and page HTML still come from the root (they
    /// aren't code), and when an admin's TV saves the config back nothing local ends up on the server.
    ///
    /// Left out on purpose: scripts/games.js. The games pages run inline scripts out of fetched HTML,
    /// which can't be transpiled, so the Games link stays off in the app.
    /// </summary>
    public sealed class KefinTweaksPatch : IJellyfinPluginPatch
    {
        // Only used when none of the configured roots can be downloaded from at install time.
        private const string FallbackRoot =
            "https://raw.githubusercontent.com/ranaldsgift/KefinTweaks/v0.5.2/";

        private const string LocalDir = "kefinTweaks";

        // Fetched by the installer or injector by name rather than through the script definitions.
        // The loader only exists from 0.5 on.
        private static readonly string[] EntryScripts =
        {
            "injector.js",
            "kefinTweaks-default-config.js",
            "scripts/kefinTweaks-loader.js",
        };

        // Loaded by modules outside the script definitions (configuration UI, seasonal animations).
        private static readonly string[] ExtraModules =
        {
            "scripts/configuration.js",
            "scripts/snowverlay.js",
            "scripts/seasonal/seasonalOverlay.js",
        };

        private static readonly string[] SkippedModules = { "scripts/games.js" };

        private const string RedirectTemplate = @"(function () {
    var LOCAL = 'plugin_cache/kefinTweaks/';
    var ROOTS = __ROOTS__;
    var KNOWN = /^https?:\/\/(?:cdn\.jsdelivr\.net\/gh\/ranaldsgift\/KefinTweaks@[^\/]+|raw\.githubusercontent\.com\/ranaldsgift\/KefinTweaks\/[^\/]+|ranaldsgift\.github\.io\/KefinTweaks)\//i;
    function relative(url) {
        var known = url.match(KNOWN);
        if (known) {
            return url.substring(known[0].length);
        }
        var cfg = window.KefinTweaksConfig || {};
        var roots = [cfg.kefinTweaksRootResolved, cfg.kefinTweaksRoot].concat(ROOTS);
        for (var i = 0; i < roots.length; i++) {
            var root = roots[i];
            if (typeof root !== 'string' || !root) {
                continue;
            }
            if (root.charAt(root.length - 1) !== '/') {
                root += '/';
            }
            if (url.indexOf(root) === 0) {
                return url.substring(root.length);
            }
        }
        return null;
    }
    function local(url) {
        if (typeof url !== 'string') {
            return url;
        }
        var path = relative(url);
        if (path === null) {
            return url;
        }
        var parts = path.split('#')[0].split('?')[0].split('/');
        var stack = [];
        for (var i = 0; i < parts.length; i++) {
            if (parts[i] === '..') {
                stack.pop();
            } else if (parts[i] && parts[i] !== '.') {
                stack.push(parts[i]);
            }
        }
        path = stack.join('/');
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
            string publicJsPath = Path.Combine(ctx.PluginCacheDir, "public.js");
            if (!File.Exists(publicJsPath))
                return;

            string publicJs = await File.ReadAllTextAsync(publicJsPath, Encoding.UTF8);
            var roots = FindRoots(publicJs);
            if (roots.Count == 0)
                return;

            Trace.WriteLine($"⚙ KefinTweaks: found in public.js, roots: {string.Join(", ", roots)}");

            // Injected before anything is downloaded: whatever fails below, no KefinTweaks script may
            // load from its root untranspiled. A script that isn't cached then simply doesn't load.
            await WriteRedirectAsync(ctx, roots);

            string? source = null;
            foreach (var root in roots.Select(r => ToDownloadRoot(ctx.ServerUrl, r)).Append(FallbackRoot).Distinct())
            {
                if (await CacheAsync(ctx, root, "kefinTweaks-plugin.js") != null)
                {
                    source = root;
                    break;
                }
            }

            if (source == null)
            {
                Trace.WriteLine("⚠ KefinTweaks: kefinTweaks-plugin.js could not be downloaded from any root, KefinTweaks won't load in the app.");
                return;
            }

            if (source == FallbackRoot)
                Trace.WriteLine($"⚠ KefinTweaks: the server's root could not be reached, caching {FallbackRoot} instead.");

            var modules = new SortedSet<string>(ExtraModules, StringComparer.Ordinal);
            foreach (var entry in EntryScripts)
            {
                var text = await CacheAsync(ctx, source, entry);
                if (text == null)
                    continue;

                foreach (Match m in RegexPatterns.KefinTweaks.ScriptEntry.Matches(text))
                    modules.Add(NormalizePath("scripts/" + m.Groups[1].Value));
                foreach (Match m in RegexPatterns.KefinTweaks.ThirdPartyEntry.Matches(text))
                    modules.Add(NormalizePath("scripts/" + m.Groups[1].Value));
            }

            foreach (var skipped in SkippedModules.Where(modules.Remove))
                Trace.WriteLine($"ℹ KefinTweaks: {skipped} left out (runs inline scripts from page HTML, which can't be transpiled).");

            int cached = 0;
            foreach (var module in modules)
            {
                if (await CacheAsync(ctx, source, module) != null)
                    cached++;
            }

            Trace.WriteLine($"✓ KefinTweaks: {cached} of {modules.Count} modules cached from {source}");
        }

        /// <summary>
        /// The roots public.js points at, in the order the loader prefers them: the resolved commit,
        /// the admin's choice, then the folder the bootstrap loads the installer from.
        /// </summary>
        private static List<string> FindRoots(string publicJs)
        {
            var config = RegexPatterns.KefinTweaks.ConfigRoot.Matches(publicJs)
                .Where(m => !string.IsNullOrWhiteSpace(m.Groups[2].Value))
                .ToList();

            var roots = config.Where(m => m.Groups[1].Value == "kefinTweaksRootResolved")
                .Concat(config.Where(m => m.Groups[1].Value == "kefinTweaksRoot"))
                .Select(m => m.Groups[2].Value.Trim())
                .ToList();

            foreach (Match m in RegexPatterns.KefinTweaks.Bootstrap.Matches(publicJs))
                roots.Add(m.Groups[1].Value.Trim());

            return roots
                .Where(r => r.Length > 0)
                .Select(r => r.EndsWith('/') ? r : r + "/")
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>A root as the web client sees it, made absolute against the server for downloading.</summary>
        private static string ToDownloadRoot(string serverUrl, string root)
            => UrlHelper.IsValidHttpUrl(root) ? root : UrlHelper.GetAbsoluteUri(serverUrl, root).ToString();

        private static async Task WriteRedirectAsync(PluginPatchContext ctx, List<string> roots)
        {
            var dir = Path.Combine(ctx.PluginCacheDir, LocalDir);
            Directory.CreateDirectory(dir);

            var script = RedirectTemplate.Replace("__ROOTS__", JsonSerializer.Serialize(roots));
            await File.WriteAllTextAsync(Path.Combine(dir, "redirect.js"), script, Encoding.UTF8);

            const string redirectSrc = "plugin_cache/kefinTweaks/redirect.js";
            ctx.InjectScript($"<script src=\"{redirectSrc}\"></script>", redirectSrc);
        }

        /// <summary>Downloads and transpiles one script; returns the transpiled text, or null when it failed.</summary>
        private static async Task<string?> CacheAsync(PluginPatchContext ctx, string root, string relPath)
        {
            var localPath = await ctx.PluginManager.DownloadAndTranspileAsync(
                root + relPath,
                ctx.PluginCacheDir,
                Path.Combine(LocalDir, relPath.Replace('/', Path.DirectorySeparatorChar)));

            return localPath == null ? null : await File.ReadAllTextAsync(localPath, Encoding.UTF8);
        }

        /// <summary>Collapses "." and ".." the way the browser resolves "scripts/../configuration/x.js".</summary>
        private static string NormalizePath(string path)
        {
            var stack = new List<string>();
            foreach (var part in path.Split('/'))
            {
                if (part == "..")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                }
                else if (part.Length > 0 && part != ".")
                {
                    stack.Add(part);
                }
            }
            return string.Join("/", stack);
        }
    }
}
