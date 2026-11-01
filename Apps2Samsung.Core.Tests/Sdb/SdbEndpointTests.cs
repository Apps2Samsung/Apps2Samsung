using System;
using System.Threading.Tasks;
using Apps2Samsung.Sdb;

namespace Apps2Samsung.Tests.Sdb;

/// <summary>
/// Which port a TV is dialed on (TV-Store#52): 26101 unless the TV's Developer Mode points at
/// 127.0.0.1, where sdbd takes only loopback and TV-Store's relay on the TV is the way in.
/// </summary>
public class SdbEndpointTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData(" 127.0.0.1 ", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LoopbackIsTheRelaySetting(string? developerIp, bool loopback)
    {
        Assert.Equal(loopback, SdbEndpoint.IsLoopback(developerIp));
    }

    [Fact]
    public void ARememberedLoopbackHostDialsTheRelayPort()
    {
        const string tv = "203.0.113.10";
        SdbEndpoint.Remember(tv, "127.0.0.1");
        Assert.True(SdbEndpoint.UsesRelay(tv));
        Assert.Equal(SdbEndpoint.DefaultRelayPort, SdbEndpoint.PortFor(tv));
        Assert.Equal("203.0.113.10:26201 (TV-Store relay)", SdbEndpoint.Describe(tv, SdbEndpoint.PortFor(tv)));

        SdbEndpoint.Remember(tv, "192.168.1.20");
        Assert.False(SdbEndpoint.UsesRelay(tv));
        Assert.Equal(SdbEndpoint.DirectPort, SdbEndpoint.PortFor(tv));
        Assert.Equal("203.0.113.10:26101", SdbEndpoint.Describe(tv, SdbEndpoint.PortFor(tv)));
    }

    [Fact]
    public void AnUnknownTvIsDialedDirectly()
    {
        const string tv = "203.0.113.11";
        SdbEndpoint.Forget(tv);
        Assert.False(SdbEndpoint.UsesRelay(tv));
        Assert.Equal(SdbEndpoint.DirectPort, SdbEndpoint.PortFor(tv));
    }

    [Fact]
    public void ATvCanHaveItsOwnRelayPort()
    {
        const string tv = "203.0.113.12";
        SdbEndpoint.SetRelayPort(tv, 26301);
        SdbEndpoint.Remember(tv, "127.0.0.1");
        Assert.Equal(26301, SdbEndpoint.PortFor(tv));
        Assert.Equal(SdbEndpoint.DefaultRelayPort, SdbEndpoint.RelayPort("203.0.113.13"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdbEndpoint.SetRelayPort(tv, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SdbEndpoint.SetRelayPort(tv, 70000));
    }

    [Fact]
    public async Task ARecentAnswerIsNotAskedAgain()
    {
        // 203.0.113.0/24 is TEST-NET: nothing answers there, so a real read would wait out its
        // timeout. A fresh Remember() must make ResolvePortAsync answer at once from memory.
        const string tv = "203.0.113.14";
        SdbEndpoint.Remember(tv, "127.0.0.1");
        var started = DateTime.UtcNow;
        var port = await SdbEndpoint.ResolvePortAsync(tv);
        Assert.Equal(SdbEndpoint.DefaultRelayPort, port);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "ResolvePortAsync went to the network although the answer was fresh");
    }

    [Fact]
    public void TheHintNamesThePortAndTheFix()
    {
        var hint = SdbEndpoint.RelayDownHint("192.168.1.50", 26201);
        Assert.Contains("26201", hint);
        Assert.Contains("Connect from Apps2Samsung", hint);
        Assert.Contains("0.3.14", hint);
        Assert.Contains("192.168.1.50", hint);
    }
}
