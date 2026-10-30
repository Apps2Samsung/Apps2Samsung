using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.Tests.Catalog;

// The "Needs Tizen X" verdicts the heads show (TV-Store#47): lenient on unknowns, lowest version of
// a grouped app, and the build the sheet lands on.
public class TizenRequirementTests
{
    private static Asset A(string? required, string name = "a.wgt") => new() { FileName = name, RequiredVersion = required };

    [Theory]
    [InlineData("6.0", "4.0", true)]
    [InlineData("6.0", "6.0", false)]
    [InlineData("2.3", "4.0", false)]
    [InlineData(null, "4.0", false)]
    [InlineData("6.0", null, false)]
    [InlineData("x", "4.0", false)]
    public void NeedsNewer_IsLenient(string? required, string? tv, bool expected) =>
        Assert.Equal(expected, TizenRequirement.NeedsNewer(A(required), tv is null ? null : Version.Parse(tv)));

    [Fact]
    public void Lowest_PicksTheSmallestWrittenVersion()
    {
        Assert.Equal("4.0", TizenRequirement.Lowest(new[] { A("6.5"), A("4.0"), A(null), A("6.0") }));
        Assert.Null(TizenRequirement.Lowest(new[] { A(null), A("junk") }));
    }

    [Fact]
    public void NoneFits_OnlyWhenTheTvIsKnownAndEveryBuildIsTooNew()
    {
        var release = new GitHubRelease { Assets = { A("6.5", "a"), A("6.0", "b") } };
        Assert.True(TizenRequirement.NoneFits(release, new Version(4, 0)));
        Assert.False(TizenRequirement.NoneFits(release, new Version(6, 0)));
        Assert.False(TizenRequirement.NoneFits(release, null));
        release.Assets.Add(A(null, "c"));
        Assert.False(TizenRequirement.NoneFits(release, new Version(4, 0)));
    }

    [Fact]
    public void FirstFitting_LandsOnABuildTheTvRuns()
    {
        var tooNew = A("6.5", "new"); var fits = A("4.0", "old");
        var assets = new[] { tooNew, fits };
        Assert.Same(fits, TizenRequirement.FirstFitting(assets, tooNew, new Version(4, 0)));
        Assert.Same(tooNew, TizenRequirement.FirstFitting(assets, tooNew, new Version(7, 0)));
        Assert.Same(tooNew, TizenRequirement.FirstFitting(assets, tooNew, null));
        Assert.Same(tooNew, TizenRequirement.FirstFitting(new[] { tooNew }, tooNew, new Version(4, 0)));
    }
}
