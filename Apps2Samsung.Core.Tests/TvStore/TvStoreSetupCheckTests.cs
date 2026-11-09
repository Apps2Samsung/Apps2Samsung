using Apps2Samsung.Localization;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

/// <summary>The TV-Store section's setup check (#1041): what was read off the TV, as lines.</summary>
public class TvStoreSetupCheckTests
{
    private const string Tv = "192.168.1.50";
    private static readonly string[] Here = { "192.168.1.20", "fe80::1" };

    [Theory]
    [InlineData("127.0.0.1", TvStoreHostIp.Relay)]
    [InlineData("192.168.1.50", TvStoreHostIp.TvItself)]
    [InlineData(" 192.168.1.50 ", TvStoreHostIp.TvItself)]
    [InlineData("192.168.1.20", TvStoreHostIp.ThisDevice)]
    [InlineData("192.168.1.99", TvStoreHostIp.Elsewhere)]
    [InlineData("", TvStoreHostIp.Unknown)]
    [InlineData(null, TvStoreHostIp.Unknown)]
    public void ClassifiesHostPcIp(string? hostPcIp, TvStoreHostIp expected)
    {
        Assert.Equal(expected, TvStoreSetupCheck.ClassifyHostIp(hostPcIp, Tv, Here));
    }

    [Fact]
    public void InstalledWithRelayIsAllGreen()
    {
        var lines = TvStoreSetupCheck.Lines(new TvStoreOverview(TvStoreInstallState.Installed, TvStorePackage.AppId, "0.3.14", "127.0.0.1", null), Tv, Here);

        Assert.Equal(new[] { "tvStoreCheckInstalled", "tvStoreCheckHostRelay" }, lines.Select(l => l.Key));
        Assert.All(lines, l => Assert.Equal(TvStoreCheckLevel.Ok, l.Level));
        Assert.Equal("0.3.14", lines[0].Args[0]);
    }

    [Fact]
    public void HostPcIpOnThisDeviceIsAWarningThatNamesIt()
    {
        var lines = TvStoreSetupCheck.Lines(new TvStoreOverview(TvStoreInstallState.Installed, TvStorePackage.AppId, null, "192.168.1.20", null), Tv, Here);

        Assert.Equal("tvStoreCheckHostThisDevice", lines[1].Key);
        Assert.Equal(TvStoreCheckLevel.Warn, lines[1].Level);
        Assert.Equal("192.168.1.20", lines[1].Args[0]);
        Assert.Equal("?", lines[0].Args[0]);
    }

    [Theory]
    [InlineData(TvStoreInstallState.NotInstalled, "tvStoreCheckNotInstalled", TvStoreCheckLevel.Bad)]
    [InlineData(TvStoreInstallState.Outdated, "tvStoreCheckOutdated", TvStoreCheckLevel.Bad)]
    public void MissingOrOldTvStoreIsBlocking(TvStoreInstallState state, string key, TvStoreCheckLevel level)
    {
        var line = TvStoreSetupCheck.Lines(new TvStoreOverview(state, null, null, null, null), Tv, Here)[0];

        Assert.Equal(key, line.Key);
        Assert.Equal(level, line.Level);
    }

    [Fact]
    public void AnUnreadableListingIsUnknownWithTheReason()
    {
        var lines = TvStoreSetupCheck.Lines(new TvStoreOverview(null, null, null, "192.168.1.99", "connection reset"), Tv, Here);

        Assert.Equal("tvStoreCheckUnknown", lines[0].Key);
        Assert.Equal(TvStoreCheckLevel.Unknown, lines[0].Level);
        Assert.Equal("connection reset", lines[0].Args[0]);
        Assert.Equal("tvStoreCheckHostElsewhere", lines[1].Key);
        Assert.Equal(TvStoreCheckLevel.Bad, lines[1].Level);
    }

    [Fact]
    public void EveryLineKeyIsInEnglish()
    {
        var english = new LocalizationCatalog();
        foreach (var key in new[] { "tvStoreCheckInstalled", "tvStoreCheckOutdated", "tvStoreCheckNotInstalled", "tvStoreCheckUnknown",
                     "tvStoreCheckHostRelay", "tvStoreCheckHostTv", "tvStoreCheckHostThisDevice", "tvStoreCheckHostElsewhere", "tvStoreCheckHostUnknown" })
            Assert.NotEqual(key, english.GetString(key));
    }
}
