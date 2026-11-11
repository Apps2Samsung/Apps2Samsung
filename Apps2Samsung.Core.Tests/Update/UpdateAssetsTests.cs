using Apps2Samsung.Update;

namespace Apps2Samsung.Tests.Update;

public class UpdateAssetsTests
{
    // In the order the GitHub API lists them (by name): before 3.3.0 the CLI archive came first.
    private static readonly string[] V321 =
    {
        "Apps2Samsung-cli-v3.2.1-linux-x64.tar.gz",
        "Apps2Samsung-cli-v3.2.1-win-arm64.zip",
        "Apps2Samsung-cli-v3.2.1-win-x64.zip",
        "Apps2Samsung-v3.2.1-android.apk",
        "Apps2Samsung-v3.2.1-linux-x64.AppImage",
        "Apps2Samsung-v3.2.1-linux-x64.tar.gz",
        "Apps2Samsung-v3.2.1-macos-arm64.tar.gz",
        "Apps2Samsung-v3.2.1-win-x64.msi",
        "Apps2Samsung-v3.2.1-win-x64.zip",
    };

    [Theory]
    [InlineData("win-x64", "Apps2Samsung-v3.2.1-win-x64.zip")]
    [InlineData("linux-x64", "Apps2Samsung-v3.2.1-linux-x64.tar.gz")]
    [InlineData("macos-arm64", "Apps2Samsung-v3.2.1-macos-arm64.tar.gz")]
    public void PicksTheAppArchive_NotTheCli(string platform, string expected)
    {
        Assert.Equal(expected, V321.First(n => UpdateAssets.IsDesktopArchive(n, platform)));
    }

    [Theory]
    [InlineData("Apps2Samsung.Cli-v3.3.0-win-x64.zip")]
    [InlineData("Apps2Samsung-cli-v3.2.1-win-x64.zip")]
    [InlineData("Apps2Samsung.Cli-v3.3.0-beta-linux-x64.tar.gz")]
    public void CliArchives_NeverMatch(string name)
    {
        Assert.False(UpdateAssets.IsDesktopArchive(name, "win-x64"));
        Assert.False(UpdateAssets.IsDesktopArchive(name, "linux-x64"));
        Assert.False(UpdateAssets.IsAnyDesktopArchive(name));
    }

    [Fact]
    public void ABetaArchive_Matches()
    {
        Assert.True(UpdateAssets.IsDesktopArchive("Apps2Samsung-v3.3.0-beta-win-x64.zip", "win-x64"));
    }

    [Fact]
    public void WithoutAnAppArchiveForThePlatform_NothingMatches()
    {
        // win-arm64 ships only the CLI; the updater must not fall into it.
        Assert.DoesNotContain(V321, n => UpdateAssets.IsDesktopArchive(n, "win-arm64"));
        Assert.DoesNotContain(V321, n => UpdateAssets.IsDesktopArchive(n, "linux-arm64"));
    }

    [Theory]
    [InlineData("Apps2Samsung-v3.2.1-win-x64.msi")]
    [InlineData("Apps2Samsung-v3.2.1-linux-x64.AppImage")]
    [InlineData(null)]
    [InlineData("")]
    public void NonArchives_NeverMatch(string? name)
    {
        Assert.False(UpdateAssets.IsDesktopArchive(name, "win-x64"));
        Assert.False(UpdateAssets.IsAnyDesktopArchive(name));
    }
}
