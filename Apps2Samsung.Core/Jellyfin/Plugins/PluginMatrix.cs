using Apps2Samsung.Models;
using System;
using System.Collections.Generic;

namespace Apps2Samsung.Helpers.Jellyfin.Plugins
{
    public static class PluginMatrix
    {
        public static readonly List<PluginMatrixEntry> Matrix =
        [
            new PluginMatrixEntry
            {
                Name = "Jellyfin Enhanced",
                FallbackUrls = new(),
                ExplicitServerFiles = new List<string>
                {
                    "/JellyfinEnhanced/script",
                    "/JellyfinEnhanced/js/splashscreen.js",
                    "/JellyfinEnhanced/js/reviews.js",
                    "/JellyfinEnhanced/js/qualitytags.js",
                    "/JellyfinEnhanced/js/plugin.js",
                    "/JellyfinEnhanced/js/pausescreen.js",
                    "/JellyfinEnhanced/js/migrate.js",
                    "/JellyfinEnhanced/js/letterboxd-links.js",
                    "/JellyfinEnhanced/js/languagetags.js",
                    "/JellyfinEnhanced/js/genretags.js",
                    "/JellyfinEnhanced/js/elsewhere.js",
                    "/JellyfinEnhanced/js/arr-tag-links.js",
                    "/JellyfinEnhanced/js/arr-links.js",
                    "/JellyfinEnhanced/js/enhanced/config.js",
                    "/JellyfinEnhanced/js/enhanced/events.js",
                    "/JellyfinEnhanced/js/enhanced/features.js",
                    "/JellyfinEnhanced/js/enhanced/helpers.js",
                    "/JellyfinEnhanced/js/enhanced/playback.js",
                    "/JellyfinEnhanced/js/enhanced/subtitles.js",
                    "/JellyfinEnhanced/js/enhanced/themer.js",
                    "/JellyfinEnhanced/js/enhanced/ui.js",
                    "/JellyfinEnhanced/js/jellyseerr/api.js",
                    "/JellyfinEnhanced/js/jellyseerr/jellyseerr.js",
                    "/JellyfinEnhanced/js/jellyseerr/modal.js",
                    "/JellyfinEnhanced/js/jellyseerr/ui.js"
                }
            },
            new PluginMatrixEntry
            {
                Name = "Media Bar",
                FallbackUrls = new List<string>
                {
                    "https://cdn.jsdelivr.net/gh/IAmParadox27/jellyfin-plugin-media-bar@main/slideshowpure.js"
                },
                UseBabel = true
            },
            new PluginMatrixEntry
            {
                Name = "EditorsChoice",
                FallbackUrls = new List<string>
                {
                    "https://raw.githubusercontent.com/lachlandcp/jellyfin-editors-choice-plugin/refs/heads/main/EditorsChoicePlugin/Api/client.js"
                },
                UseBabel = false
            },
            new PluginMatrixEntry
            {
                Name = "Home Screen Sections",
                FallbackUrls = new List<string>
                {
                    "https://raw.githubusercontent.com/IAmParadox27/jellyfin-plugin-home-sections/main/src/Jellyfin.Plugin.HomeScreenSections/Inject/HomeScreenSections.js"
                },
                UseBabel = true
            },
            new PluginMatrixEntry
            {
                Name = "Plugin Pages",
                FallbackUrls = new(),
                UseBabel = true
            },
            new PluginMatrixEntry
            {
                Name = "KefinTweaks",
                FallbackUrls = new List<string>
                {
                    "https://cdn.jsdelivr.net/gh/ranaldsgift/KefinTweaks@latest/kefinTweaks-plugin.js"
                },
                RawRoot = "https://raw.githubusercontent.com/ranaldsgift/KefinTweaks/v0.4.5/",
                UseBabel = true
            }
        ];
        public static readonly List<ServerAssetRule> ServerAssetRules =
        [
            new ServerAssetRule(
                pluginName: "GenericPluginAsset",
                match: url => url.Contains("/plugins/", StringComparison.OrdinalIgnoreCase),
                treatAs: ServerAssetKind.PluginAsset),

            new ServerAssetRule("EditorsChoice", url => url.Contains("editorschoice", StringComparison.OrdinalIgnoreCase), ServerAssetKind.PluginAsset),
            new ServerAssetRule("KefinTweaks", url => url.Contains("kefin", StringComparison.OrdinalIgnoreCase), ServerAssetKind.PluginAsset),
            new ServerAssetRule("Media Bar", url => url.Contains("mediabar", StringComparison.OrdinalIgnoreCase), ServerAssetKind.PluginAsset),
            new ServerAssetRule("Home Screen Sections", url => url.Contains("homescreensections", StringComparison.OrdinalIgnoreCase), ServerAssetKind.PluginAsset),
            new ServerAssetRule("Jellyfin Enhanced", url => url.Contains("jellyfinenhanced", StringComparison.OrdinalIgnoreCase) || url.Contains("/JellyfinEnhanced/", StringComparison.OrdinalIgnoreCase), ServerAssetKind.PluginAsset),
        ];

        public static List<PluginMatrixEntry> GetMatrix() => Matrix;
    }
}