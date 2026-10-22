using System;
using System.Collections.Generic;
using System.Linq;
using Apps2Samsung.Models;

namespace Apps2Samsung.Catalog
{
    /// <summary>One group of the Install screen's catalog: a category, or the search results.</summary>
    /// <param name="CategoryId">The category id, or <see cref="CatalogGroups.SearchId"/>.</param>
    /// <param name="Title">What the group title says.</param>
    /// <param name="Count">How many apps the category holds in all.</param>
    /// <param name="Rows">The apps shown: the first few, or all of them when expanded.</param>
    /// <param name="IsTruncated">True when the group holds more than it shows.</param>
    /// <param name="IsExpanded">True when the group shows everything it holds although it could truncate.</param>
    public sealed record CatalogGroup(
        string CategoryId,
        string Title,
        int Count,
        IReadOnlyList<GitHubRelease> Rows,
        bool IsTruncated,
        bool IsExpanded)
    {
        /// <summary>Whether the group gets a "Show all" / "Show less" action.</summary>
        public bool CanToggle => IsTruncated || IsExpanded;
    }

    /// <summary>
    /// The catalog as short groups (#755, the Install screen): one group per category with the
    /// first few apps and a "Show all" that expands it in place, so seventy apps are a screen of
    /// headings instead of a long scroll. A search replaces the groups with one flat result list
    /// across every category. Both heads build their list from this.
    /// </summary>
    public static class CatalogGroups
    {
        /// <summary>How many apps a collapsed group shows.</summary>
        public const int PerGroup = 3;
        /// <summary>The category id of the search-results group.</summary>
        public const string SearchId = "search";
        /// <summary>The category id of the favorites group.</summary>
        public const string FavoritesId = "favorites";

        /// <param name="favorites">App names the user starred: they lead the list as a group of
        /// their own, never truncated, while the categories keep them too.</param>
        public static IReadOnlyList<CatalogGroup> Build(
            IReadOnlyCollection<GitHubRelease> all,
            string? query,
            ISet<string> expanded,
            Func<string, string> localize,
            ISet<string>? favorites = null)
        {
            var q = (query ?? string.Empty).Trim();
            if (q.Length > 0)
            {
                var hits = all.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                return new[] { new CatalogGroup(SearchId, localize("lblResults"), hits.Count, hits, false, true) };
            }

            var groups = new List<CatalogGroup>();
            if (favorites is { Count: > 0 })
            {
                var starred = all.Where(r => favorites.Contains(r.Name)).ToList();
                if (starred.Count > 0)
                    groups.Add(new CatalogGroup(FavoritesId, localize("lblFavorites"), starred.Count, starred, false, false));
            }
            foreach (var (id, count) in CommunityAppList.Counts(all))
            {
                if (id == AppCategories.All)
                    continue;
                var rows = CommunityAppList.Filter(all, id).ToList();
                var isExpanded = expanded.Contains(id);
                var truncated = rows.Count > PerGroup && !isExpanded;
                groups.Add(new CatalogGroup(
                    id,
                    AppCategories.DisplayName(id, localize),
                    count,
                    truncated ? rows.Take(PerGroup).ToList() : rows,
                    truncated,
                    isExpanded && rows.Count > PerGroup));
            }
            return groups;
        }
    }
}
