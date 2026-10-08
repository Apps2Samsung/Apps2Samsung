using System;
using System.Collections.Generic;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The fixed set of app categories the installer filters on. The ids are the ones the
    /// community catalog (<c>catalog.json</c>) and the provider manifest (<c>category</c> on a
    /// provider) use; each id has a localization key for the heads and an English fallback for
    /// an id a translation file does not know yet.
    /// </summary>
    public static class AppCategories
    {
        public const string Media = "media";
        public const string Iptv = "iptv";
        public const string Streaming = "streaming";
        public const string Games = "games";
        public const string Casting = "casting";
        public const string Tools = "tools";
        public const string Other = "other";

        /// <summary>Pseudo-id of the unfiltered list.</summary>
        public const string All = "all";

        /// <summary>Display order of the filter dropdown (after "All categories").</summary>
        public static readonly IReadOnlyList<string> Ordered = new[]
        {
            Media, Iptv, Streaming, Games, Casting, Tools, Other,
        };

        private static readonly Dictionary<string, (string Key, string English)> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            [All] = ("lblAllCategories", "All categories"),
            [Media] = ("categoryMedia", "Media servers & players"),
            [Iptv] = ("categoryIptv", "IPTV & Live TV"),
            [Streaming] = ("categoryStreaming", "Streaming"),
            [Games] = ("categoryGames", "Games & emulators"),
            [Casting] = ("categoryCasting", "Casting & cameras"),
            [Tools] = ("categoryTools", "Tools & system"),
            [Other] = ("categoryOther", "Other"),
        };

        /// <summary>Maps anything the catalog or manifest says to a known id; blank or unknown becomes "other".</summary>
        public static string Normalize(string? id)
            => !string.IsNullOrWhiteSpace(id) && Names.ContainsKey(id) && id != All ? id.ToLowerInvariant() : Other;

        /// <summary>The en.json key for a category id (or the "all" pseudo-id).</summary>
        public static string LocalizationKey(string id) => Names.TryGetValue(id, out var n) ? n.Key : Names[Other].Key;

        /// <summary>English name, used when the active language has no string for the key.</summary>
        public static string EnglishName(string id) => Names.TryGetValue(id, out var n) ? n.English : Names[Other].English;

        /// <summary>
        /// Localized name: the head's translation for the key, unless that lookup just echoes the
        /// key back (no translation yet), in which case the English name.
        /// </summary>
        public static string DisplayName(string id, Func<string, string> localize)
        {
            var key = LocalizationKey(id);
            var text = localize(key);
            return string.IsNullOrWhiteSpace(text) || text == key ? EnglishName(id) : text;
        }
    }
}
