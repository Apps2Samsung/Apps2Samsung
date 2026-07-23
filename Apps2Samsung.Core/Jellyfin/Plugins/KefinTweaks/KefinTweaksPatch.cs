using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins.KefinTweaks
{
    public sealed class KefinTweaksPatch : IJellyfinPluginPatch
    {
        private const string KefinTweaksRawRoot =
            "https://raw.githubusercontent.com/ranaldsgift/KefinTweaks/v0.4.5/";

        public async Task ApplyAsync(PluginPatchContext ctx)
        {
            string pluginCacheDir = ctx.PluginCacheDir;
            string serverUrl = ctx.ServerUrl;

            string publicJsPath = Path.Combine(pluginCacheDir, "public.js");
            if (!File.Exists(publicJsPath))
            {
                Trace.WriteLine("▶ KefinTweaks: public.js not found, skipping.");
                return;
            }

            string js = await File.ReadAllTextAsync(publicJsPath, Encoding.UTF8);

            if (!RegexPatterns.KefinTweaks.Loader.IsMatch(js))
            {
                Trace.WriteLine("▶ KefinTweaks: loader not found, skipping.");
                return;
            }

            Trace.WriteLine("⚙ KefinTweaks: patching public.js");

            Directory.CreateDirectory(Path.Combine(pluginCacheDir, "kefinTweaks"));

            // --- main plugin ---
            await ctx.PluginManager.DownloadAndTranspileAsync(
                KefinTweaksRawRoot + "kefinTweaks-plugin.js",
                pluginCacheDir,
                Path.Combine("kefinTweaks", "kefinTweaks-plugin.js"));

            RewritePluginRoot(pluginCacheDir);

            // --- injector ---
            var injectorPath = await ctx.PluginManager.DownloadAndTranspileAsync(
                KefinTweaksRawRoot + "injector.js",
                pluginCacheDir,
                Path.Combine("kefinTweaks", "injector.js"));

            if (injectorPath != null)
            {
                await ProcessModulesAsync(ctx, injectorPath);
                await ProcessCssAsync(ctx);
                js = await InjectSkinCssAsync(ctx, js);
            }

            // --- rewrite loader ---
            js = RegexPatterns.KefinTweaks.Loader.Replace(
                js,
                "script.src = 'plugin_cache/kefinTweaks/kefinTweaks-plugin.js';");

            js = RegexPatterns.KefinTweaks.TweaksRoot.Replace(
                js,
                @"""kefinTweaksRoot"": ""plugin_cache/kefinTweaks/""");

            await File.WriteAllTextAsync(publicJsPath, js, Encoding.UTF8);

            Trace.WriteLine("✓ KefinTweaks patched successfully");
        }

        // ------------------------------------------------------------
        // Core helpers
        // ------------------------------------------------------------

        private static void RewritePluginRoot(string pluginCacheDir)
        {
            string path = Path.Combine(pluginCacheDir, "kefinTweaks", "kefinTweaks-plugin.js");
            if (!File.Exists(path)) return;

            var js = File.ReadAllText(path);
            js = js.Replace(
                "https://cdn.jsdelivr.net/gh/ranaldsgift/KefinTweaks",
                "plugin_cache/kefinTweaks");

            File.WriteAllText(path, js);
        }

        private async Task ProcessModulesAsync(PluginPatchContext ctx, string injectorPath)
        {
            string src = await File.ReadAllTextAsync(injectorPath, Encoding.UTF8);
            var matches = RegexPatterns.KefinTweaks.ScriptEntry.Matches(src);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
            {
                string name = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(name)) continue;
                if (name is "kefinTweaks-plugin.js" or "injector.js") continue;

                string modulePath =
                    name.Equals("jquery.flurry.min.js", StringComparison.OrdinalIgnoreCase)
                        ? "scripts/third%20party/" + name
                        : "scripts/" + name;

                string url = KefinTweaksRawRoot + modulePath;

                string relPath = Path.Combine(
                    "kefinTweaks",
                    modulePath.Replace("%20", " ")
                              .Replace("/", Path.DirectorySeparatorChar.ToString()));

                await ctx.PluginManager.DownloadAndTranspileAsync(url, ctx.PluginCacheDir, relPath);
            }

            Trace.WriteLine("✓ KefinTweaks modules cached");
        }

        private async Task ProcessCssAsync(PluginPatchContext ctx)
        {
            var cssFiles = new[]
            {
                "chromic-kefin.css",
                "elegant-kefin.css",
                "fin-kefin-10.11.css",
                "flow-kefin.css",
                "glassfin-kefin.css",
                "jamfin-kefin-10.css",
                "jamfin-kefin.css",
                "neutralfin-kefin.css",
                "scyfin-kefin.css",
                "optional/ElegantFin/solidAppBar.css",
                "optional/ElegantFin/libraryLabelVisibility.css",
                "optional/ElegantFin/extraOverlayButtons.css",
                "optional/ElegantFin/centerPlayButton.css",
                "optional/ElegantFin/cardHoverEffect.css",
            };

            Trace.WriteLine("▶ KefinTweaks: caching CSS skins");

            foreach (var file in cssFiles)
            {
                try
                {
                    string url = KefinTweaksRawRoot + "skins/" + file;

                    string local = Path.Combine(
                        ctx.PluginCacheDir,
                        "kefinTweaks",
                        "skins",
                        "css",
                        file.Replace("/", Path.DirectorySeparatorChar.ToString()));

                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);

                    var bytes = await ctx.PluginManager.DownloadBytesAsync(url);
                    if (bytes != null)
                        await File.WriteAllBytesAsync(local, bytes);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"⚠ KefinTweaks CSS failed: {file} {ex}");
                }
            }
        }

        private async Task<string> InjectSkinCssAsync(PluginPatchContext ctx, string js)
        {
            var info = await ctx.PluginManager.Api.GetPublicSystemInfoAsync(ctx.ServerUrl);
            var version = info?.Version ?? "0.0.0";
            var majorMinor = string.Join(".", version.Split('.').Take(2));

            var skin = GetKefinDefaultSkin(ctx.PluginCacheDir);
            if (string.IsNullOrWhiteSpace(skin))
                return js;

            string skinLower = skin.ToLowerInvariant();

            js = EnsureCssLinked(js,
                $"plugin_cache/kefinTweaks/skins/css/{skinLower}-kefin.css");

            js = EnsureCssLinked(js,
                $"plugin_cache/kefinTweaks/skins/css/{skinLower}-kefin-{majorMinor}.css");

            Trace.WriteLine($"✓ KefinTweaks skin injected: {skin} ({majorMinor})");

            return js;
        }


        // ------------------------------------------------------------
        // Utility helpers (verbatim behavior)
        // ------------------------------------------------------------

        private static string EnsureCssLinked(string js, string href)
        {
            if (js.Contains(href, StringComparison.OrdinalIgnoreCase))
                return js;

            return js + $@"
(function () {{
    try {{
        var href = '{href}';
        if (document.querySelector('link[href=""' + href + '""]')) return;
        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        document.head.appendChild(link);
        console.log('🪼 KefinTweaks CSS injected:', href);
    }} catch (e) {{
        console.error('Failed to inject KefinTweaks CSS', e);
    }}
}})();
";
        }

        private static string? GetKefinDefaultSkin(string pluginCacheDir)
        {
            try
            {
                string configPath = Path.Combine(
                    pluginCacheDir,
                    "kefinTweaks",
                    "config",
                    "config.json");

                if (!File.Exists(configPath))
                    return null;

                var json = File.ReadAllText(configPath);
                var match = RegexPatterns.PluginConfig.DefaultSkin.Match(json);

                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
