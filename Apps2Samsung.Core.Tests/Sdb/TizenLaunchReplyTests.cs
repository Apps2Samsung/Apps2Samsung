using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

public class TizenLaunchReplyTests
{
    private const string Launched = "launch app AprZAARz4r.Jellyfin\napp_id[AprZAARz4r.Jellyfin] launch start\napp_id[AprZAARz4r.Jellyfin] launched";

    [Theory]
    [InlineData(Launched, TizenLaunchVerdict.Launched)]
    // Every platform app answers 400: was_execute only knows Smart Hub apps.
    [InlineData("launch app com.samsung.tv.hotel\napp_id[com.samsung.tv.hotel] launch failed[400]", TizenLaunchVerdict.NotASmartHubApp)]
    [InlineData("app_id[Pkg.App] launch failed[-3]", TizenLaunchVerdict.Refused)]
    [InlineData("permission denied", TizenLaunchVerdict.Refused)]
    // sdbd's whole answer to a verb it does not whitelist.
    [InlineData("closed", TizenLaunchVerdict.Refused)]
    [InlineData("launch app Pkg.App", TizenLaunchVerdict.Unknown)]
    [InlineData("", TizenLaunchVerdict.Unknown)]
    [InlineData(null, TizenLaunchVerdict.Unknown)]
    public void Parse(string? reply, TizenLaunchVerdict expected)
    {
        Assert.Equal(expected, TizenLaunchReply.Parse(reply));
    }

    [Fact]
    public void Parse_AppIdContainingAMarker_IsNotARefusal()
    {
        // The id is echoed in every reply, so a "launched" line wins over an id that reads like an error.
        const string reply = "launch app ErrorFinder.App\napp_id[ErrorFinder.App] launched";

        Assert.Equal(TizenLaunchVerdict.Launched, TizenLaunchReply.Parse(reply));
    }

    [Theory]
    [InlineData(Launched, true)]
    [InlineData("launch app Pkg.App", true)]
    [InlineData("No connection could be made because the target machine actively refused it.", false)]
    [InlineData(null, false)]
    public void IsFromLauncher(string? reply, bool expected)
    {
        Assert.Equal(expected, TizenLaunchReply.IsFromLauncher(reply));
    }

    [Fact]
    public void Summarize_DropsTheEcho()
    {
        Assert.Equal(
            "app_id[AprZAARz4r.Jellyfin] launch start · app_id[AprZAARz4r.Jellyfin] launched",
            TizenLaunchReply.Summarize(Launched));
    }

    [Fact]
    public void Summarize_OnlyTheEcho_KeepsIt()
    {
        Assert.Equal("launch app Pkg.App", TizenLaunchReply.Summarize("launch app Pkg.App\r\n"));
    }
}
