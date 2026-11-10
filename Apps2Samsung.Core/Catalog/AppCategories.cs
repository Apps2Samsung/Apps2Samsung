using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>
    /// The app categories the installer filters on (#1054). The official community catalog decides
    /// them: <c>categories</c> in <c>catalog.json</c> gives the ids, their order (the key order) and
    /// their English names, <c>category_colors</c> the tile colours. The heads hand that catalog to
    /// <see cref="Use"/> once it is loaded; until then, and for a bundle without <c>categories</c>,
    /// the seven built-in ones below apply.
    /// </summary>
    /// <remarks>
    /// Ids are stored on an app as the catalog or manifest wrote them (<see cref="Clean"/>) and only
    /// resolved against the list when shown or filtered (<see cref="Normalize"/>). Providers load in
    /// parallel with the community catalog, so an id read before the catalog arrived still lands in
    /// its own category instead of being folded into "other" for good.
    /// </remarks>
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

        /// <summary>The shape of a category id in the catalog contract.</summary>
        private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);

        private static readonly Regex ColorPattern = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

        // The ids this version has translations for; any other id shows the catalog's English name.
        private static readonly Dictionary<string, (string Key, string English)> Builtin = new(StringComparer.OrdinalIgnoreCase)
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

        private static readonly CategorySet Fallback = new(
            new[] { Media, Iptv, Streaming, Games, Casting, Tools, Other },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        // Swapped as a whole, so a reader never sees a half-built list.
        private static volatile CategorySet _current = Fallback;

        private sealed record CategorySet(
            IReadOnlyList<string> Ordered,
            IReadOnlyDictionary<string, string> Names,
            IReadOnlyDictionary<string, string> Colors);

        /// <summary>Display order of the filter dropdown (after "All categories"); "other" is always in it.</summary>
        public static IReadOnlyList<string> Ordered => _current.Ordered;

        /// <summary>
        /// Takes the categories from the official community catalog: its <c>categories</c> in key
        /// order, names and colours. A catalog without <c>categories</c> (an older bundle) brings back
        /// the built-in seven; no catalog at all (it could not be read) keeps what was there.
        /// Invalid ids and colours are skipped.
        /// </summary>
        public static void Use(CommunityCatalog? catalog)
        {
            if (catalog is null)
                return;
            var source = catalog.Categories;
            var ordered = new List<string>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source is { Count: > 0 })
            {
                foreach (var (rawId, name) in source)
                {
                    var id = (rawId ?? string.Empty).Trim().ToLowerInvariant();
                    if (!IdPattern.IsMatch(id) || id == All || names.ContainsKey(id))
                        continue;
                    ordered.Add(id);
                    names[id] = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
                }
            }
            if (ordered.Count == 0)
            {
                _current = Fallback;
                return;
            }
            // Where unknown ids land, so it has to exist even when the catalog left it out.
            if (!names.ContainsKey(Other))
                ordered.Add(Other);

            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawId, color) in catalog.CategoryColors ?? new Dictionary<string, string>())
            {
                var id = (rawId ?? string.Empty).Trim().ToLowerInvariant();
                var hex = (color ?? string.Empty).Trim();
                if (ordered.Contains(id) && ColorPattern.IsMatch(hex))
                    colors[id] = hex.ToUpperInvariant();
            }
            _current = new CategorySet(ordered, names, colors);
        }

        /// <summary>
        /// The id as an app carries it: trimmed and lower-cased when it has the shape of a category
        /// id, else "other". Does not look at the list, so it is safe before the catalog is loaded.
        /// </summary>
        public static string Clean(string? id)
        {
            var cleaned = (id ?? string.Empty).Trim().ToLowerInvariant();
            return IdPattern.IsMatch(cleaned) && cleaned != All ? cleaned : Other;
        }

        /// <summary>Maps anything the catalog or manifest says to an id in <see cref="Ordered"/>; blank or unknown becomes "other".</summary>
        public static string Normalize(string? id)
        {
            var cleaned = Clean(id);
            return _current.Ordered.Contains(cleaned) ? cleaned : Other;
        }

        /// <summary>The tile colour the catalog gives the category ("#RRGGBB"), or null when it gives none.</summary>
        public static string? CatalogColor(string? id)
            => _current.Colors.TryGetValue(Normalize(id), out var hex) ? hex : null;

        /// <summary>The en.json key for a category id (or the "all" pseudo-id); null for a category only the catalog knows.</summary>
        public static string? LocalizationKey(string id) => Builtin.TryGetValue(id, out var n) ? n.Key : null;

        /// <summary>English name: the catalog's, else the built-in one.</summary>
        public static string EnglishName(string id)
        {
            if (_current.Names.TryGetValue(id, out var name))
                return name;
            return Builtin.TryGetValue(id, out var n) ? n.English : id;
        }

        /// <summary>
        /// Localized name of the category <paramref name="id"/> resolves to: the head's translation
        /// when this version has a key for it and the language has a string, else the English name
        /// (the catalog's, so a new category needs no translation to show up).
        /// </summary>
        public static string DisplayName(string id, Func<string, string> localize)
        {
            var resolved = string.Equals(id, All, StringComparison.OrdinalIgnoreCase) ? All : Normalize(id);
            var key = LocalizationKey(resolved);
            if (key is null)
                return EnglishName(resolved);
            var text = localize(key);
            return string.IsNullOrWhiteSpace(text) || text == key ? EnglishName(resolved) : text;
        }
    }
}
