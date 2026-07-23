using System.Collections.Generic;
using Apps2Samsung.Models;

namespace Apps2Samsung.Jellyfin
{
    /// <summary>
    /// The built-in JellyThemes (<see href="https://github.com/kingchenc/JellyThemes"/>), shared by
    /// both heads so the list lives in one place. Selecting a theme writes its
    /// <see cref="JellyTheme.CssImportStatement"/> into the user's custom CSS.
    /// </summary>
    public static class JellyThemeCatalog
    {
        public const string RepoUrl = "https://github.com/kingchenc/JellyThemes";

        public static IReadOnlyList<JellyTheme> Themes { get; } = new List<JellyTheme>
        {
            new()
            {
                Name = "Obsidian", Icon = "\U0001F7E3", ColorName = "Purple", HexColor = "#6B5B95",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Obsidian/Obsidian.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Obsidian/assets/preview/Obsidian.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Obsidian",
            },
            new()
            {
                Name = "Solaris", Icon = "\U0001F7E1", ColorName = "Gold", HexColor = "#D4AF37",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Solaris/Solaris.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Solaris/assets/preview/Solaris.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Solaris",
            },
            new()
            {
                Name = "Nebula", Icon = "\U0001F535", ColorName = "Cyan", HexColor = "#00CED1",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Nebula/Nebula.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Nebula/assets/preview/Nebula.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Nebula",
            },
            new()
            {
                Name = "Ember", Icon = "\U0001F7E0", ColorName = "Orange", HexColor = "#FF6B35",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Ember/Ember.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Ember/assets/preview/Ember.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Ember",
            },
            new()
            {
                Name = "Void", Icon = "⚫", ColorName = "Black", HexColor = "#1C1C1C",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Void/Void.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Void/assets/preview/Void.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Void",
            },
            new()
            {
                Name = "Phantom", Icon = "\U0001F47B", ColorName = "Slate", HexColor = "#708090",
                CssImportUrl = "https://cdn.jsdelivr.net/gh/kingchenc/JellyThemes@master/Themes/Phantom/Phantom.css",
                PreviewUrl = "https://raw.githubusercontent.com/kingchenc/JellyThemes/master/Themes/Phantom/assets/preview/Phantom.png",
                ReadmeUrl = "https://github.com/kingchenc/JellyThemes/tree/main/Themes/Phantom",
            },
        };
    }
}
