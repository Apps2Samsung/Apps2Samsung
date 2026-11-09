using System;
using System.Collections.Generic;
using System.Globalization;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Design
{
    /// <summary>One tag on a catalog row: a word at the start of the row's grey line.</summary>
    /// <param name="Text">What the row shows, with a capital first letter.</param>
    /// <param name="IsWarning">Amber (asks for care before installing) rather than the grey of plain information.</param>
    /// <param name="Tooltip">The longer explanation, for a head that has hover.</param>
    public sealed record CatalogTag(string Text, bool IsWarning, string? Tooltip);

    /// <summary>
    /// The tags of a catalog row, the same on every head (#1041 follow-up, option B): words at the
    /// start of the grey line under the name instead of chips next to it, so a long name never pushes
    /// them into the chevron. The warnings come first (18+, root, self-updating, Needs Tizen X), then
    /// "Closed source", which is information only; the rest of the grey line shortens before a tag does.
    /// </summary>
    public static class CatalogTags
    {
        public static IReadOnlyList<CatalogTag> For(GitHubRelease release, Version? tvVersion, Func<string, string> localize)
        {
            ArgumentNullException.ThrowIfNull(release);
            ArgumentNullException.ThrowIfNull(localize);
            var tags = new List<CatalogTag>();
            if (release.IsAdult)
                tags.Add(new CatalogTag(Capital(localize("lblAdultBadge")), true, localize("lblAdultOnly")));
            if (release.IsRoot)
                tags.Add(new CatalogTag(Capital(localize("lblRootBadge")), true, localize("lblRootPackage")));
            if (release.IsSelfUpdating)
                tags.Add(new CatalogTag(Capital(localize("lblSelfUpdatingBadge")), true, localize("lblSelfUpdatingPackage")));
            if (TizenRequirement.NoneFits(release, tvVersion))
                tags.Add(new CatalogTag(string.Format(CultureInfo.CurrentCulture, localize("lblNeedsTizenBadge"), TizenRequirement.Lowest(release.Assets)), true, null));
            if (release.IsClosedSource)
                tags.Add(new CatalogTag(Capital(localize("lblClosedSourceBadge")), false, localize("lblClosedSourcePackage")));
            return tags;
        }

        // "self-updating" reads as a tag at the start of a line: "Self-updating".
        private static string Capital(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }
}
