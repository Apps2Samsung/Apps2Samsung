using Apps2Samsung.Design;
using Apps2Samsung.Localization;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Design;

/// <summary>The tags at the start of a catalog row's grey line (#1041 follow-up, option B).</summary>
public class CatalogTagsTests
{
    private static readonly Func<string, string> English = new LocalizationCatalog().GetString;

    [Fact]
    public void NoFlagsNoTags()
    {
        Assert.Empty(CatalogTags.For(new GitHubRelease { Name = "Jellyfin" }, null, English));
    }

    [Fact]
    public void WarningsComeFirstAndClosedSourceIsLastAndGrey()
    {
        var release = new GitHubRelease { Name = "Root Tools", IsClosedSource = true, IsRoot = true, IsSelfUpdating = true, IsAdult = true };

        var tags = CatalogTags.For(release, null, English);

        Assert.Equal(new[] { "18+", "Root", "Self-updating", "Closed source" }, tags.Select(t => t.Text));
        Assert.All(tags.Take(3), t => Assert.True(t.IsWarning));
        Assert.False(tags[^1].IsWarning);
        Assert.Equal("The source code is not in the linked repository", tags[^1].Tooltip);
    }
}
