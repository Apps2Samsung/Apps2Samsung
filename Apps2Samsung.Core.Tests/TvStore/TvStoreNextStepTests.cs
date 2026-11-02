using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

/// <summary>The closing message of Arm TV-Store says what is left to do on the TV (#960).</summary>
public class TvStoreNextStepTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.50", TvStoreNextStep.ChangeHostIp)]
    [InlineData("", "192.168.1.50", TvStoreNextStep.ChangeHostIp)]
    [InlineData(null, "192.168.1.50", TvStoreNextStep.ChangeHostIp)]
    [InlineData("192.168.1.50", null, TvStoreNextStep.ChangeHostIp)]
    [InlineData("192.168.1.50", "192.168.1.50", TvStoreNextStep.Done)]
    [InlineData(" 192.168.1.50 ", "192.168.1.50", TvStoreNextStep.Done)]
    [InlineData("127.0.0.1", "192.168.1.50", TvStoreNextStep.UsingRelay)]
    public void PicksTheStepFromTheHostIpTheTvReportsNow(string? hostPcIp, string? ownIp, TvStoreNextStep expected)
    {
        Assert.Equal(expected, TvStoreNextSteps.For(hostPcIp, ownIp));
    }

    [Fact]
    public void EveryStepHasItsOwnMessage()
    {
        var keys = new[] { TvStoreNextStep.ChangeHostIp, TvStoreNextStep.UsingRelay, TvStoreNextStep.Done }
            .Select(TvStoreNextSteps.MessageKey).Distinct().ToList();
        Assert.Equal(3, keys.Count);
    }
}
