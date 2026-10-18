using System;
using System.Linq;
using Apps2Samsung.Catalog;

namespace Apps2Samsung.Design
{
    /// <summary>
    /// The letter tile in front of an app row (#755): the app's first letter on a colour that
    /// says which category it is in. An app outside the known categories gets a stable colour
    /// from its name, so the same app always looks the same.
    /// </summary>
    public static class AppTiles
    {
        private static readonly string[] Palette =
        {
            "TileMedia", "TileIptv", "TileStreaming", "TileGames", "TileCasting", "TileTools", "TileOther",
        };

        /// <summary>The letter on the tile: the first letter or digit of the name, upper-cased.</summary>
        public static string Letter(string? name)
        {
            var first = (name ?? string.Empty).FirstOrDefault(char.IsLetterOrDigit);
            return first == default ? "?" : char.ToUpperInvariant(first).ToString();
        }

        /// <summary>The colour token (a <c>DesignTokens.Colors</c> name) for the tile.</summary>
        public static string ColorToken(string? category, string? name)
        {
            switch (AppCategories.Normalize(category ?? string.Empty))
            {
                case AppCategories.Media: return "TileMedia";
                case AppCategories.Iptv: return "TileIptv";
                case AppCategories.Streaming: return "TileStreaming";
                case AppCategories.Games: return "TileGames";
                case AppCategories.Casting: return "TileCasting";
                case AppCategories.Tools: return "TileTools";
            }
            if (string.IsNullOrEmpty(name))
                return "TileOther";
            uint hash = 0;
            foreach (var ch in name)
                hash = unchecked(hash * 31 + ch);
            return Palette[hash % (uint)Palette.Length];
        }
    }
}
