using System.Runtime.InteropServices;
using Apps2Samsung.Cli.Commands;

namespace Apps2Samsung.Cli.Tests;

public sealed class SelfUpdateTests
{
    [Fact]
    public void CheckAndChannel_AreParsed()
    {
        var (o, error) = CliOptions.Parse(new[] { "self-update", "--check", "--channel", "Beta" });
        Assert.Null(error);
        Assert.Equal("self-update", o.Command);
        Assert.True(o.Check);
        Assert.Equal("beta", o.Channel);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData(null)]
    public void Channel_TakesOnlyStableOrBeta(string? value)
    {
        var args = value is null ? new[] { "self-update", "--channel" } : new[] { "self-update", "--channel", value };
        var (_, error) = CliOptions.Parse(args);
        Assert.NotNull(error);
    }

    [Theory]
    // The Termux build is assemblies run by Termux's dotnet, whatever the machine says it is.
    [InlineData("/data/data/com.termux/files/usr/bin/dotnet", false, false, Architecture.Arm64, "linux-bionic-arm64")]
    [InlineData("/home/me/bin/apps2samsung-cli", false, false, Architecture.Arm64, "linux-arm64")]
    [InlineData("/home/me/bin/apps2samsung-cli", false, false, Architecture.X64, "linux-x64")]
    [InlineData("/Users/me/apps2samsung-cli", false, true, Architecture.Arm64, "osx-arm64")]
    [InlineData(@"C:\Tools\apps2samsung-cli.exe", true, false, Architecture.X64, "win-x64")]
    [InlineData(null, true, false, Architecture.Arm64, "win-arm64")]
    public void Rid_NamesTheArchiveOfThisBuild(string? processPath, bool windows, bool macOs, Architecture arch, string expected)
    {
        Assert.Equal(expected, SelfUpdateCommand.Rid(processPath, windows, macOs, arch));
    }

    [Theory]
    [InlineData("3.3.1-beta+764529c87355b9a29b7210b058e75e06c632cdec", "3.3.1-beta", "764529c87355b9a29b7210b058e75e06c632cdec")]
    [InlineData("3.3.0", "3.3.0", null)]
    [InlineData("0.0.0-dev+local", "0.0.0-dev", null)]
    [InlineData(null, "0.0.0", null)]
    public void Split_SeparatesTheVersionFromTheBuildCommit(string? informational, string version, string? commit)
    {
        Assert.Equal((version, commit), SelfUpdateCommand.Split(informational));
    }

    [Theory]
    // --channel wins, then the setting shared with the desktop, then what this build is.
    [InlineData("stable", true, "3.3.1-beta", false)]
    [InlineData("beta", false, "3.3.0", true)]
    [InlineData(null, false, "3.3.1-beta", false)]
    [InlineData(null, true, "3.3.0", true)]
    [InlineData(null, null, "3.3.1-beta", true)]
    [InlineData(null, null, "3.3.0", false)]
    public void FollowsBeta_ByChoiceThenSettingThenBuild(string? channel, bool? setting, string version, bool expected)
    {
        Assert.Equal(expected, SelfUpdateCommand.FollowsBeta(channel, setting, version));
    }

    [Fact]
    public void Describe_SaysWhenABetaIsAheadOfTheStableReleases()
    {
        var state = new SelfUpdateCommand.State("v3.3.1-beta", "v3.3.0", false, false, false, "linux-x64", null, "https://example.invalid", null);
        Assert.Contains("is newer than", SelfUpdateCommand.Describe(state)[0]);
    }

    [Fact]
    public void BetaSetting_RoundTripsAsTheDesktopsBoolean()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2s-selfupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(new DesktopSettings(dir).IncludeBetaUpdates);
            new DesktopSettings(dir).IncludeBetaUpdates = true;
            Assert.True(new DesktopSettings(dir).IncludeBetaUpdates);
            // A boolean, as the desktop's AppSettings reads it; a string would fail its load.
            Assert.Contains("\"IncludeBetaUpdates\": true", File.ReadAllText(Path.Combine(dir, "settings.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
