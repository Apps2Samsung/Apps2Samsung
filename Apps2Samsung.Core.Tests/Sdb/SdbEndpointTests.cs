using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Models;
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

    [Fact]
    public void ADroppedRelayHintIsNotTheRelayDownHint()
    {
        var dropped = SdbEndpoint.RelayDroppedHint("203.0.113.10", 26201);
        Assert.Contains(SdbEndpoint.RelayHintMarker, dropped);
        Assert.Contains(SdbEndpoint.RelayHintMarker, SdbEndpoint.RelayDownHint("203.0.113.10", 26201));
        Assert.DoesNotContain("nothing answered", dropped);
    }
}

/// <summary>A failed command through the relay says what the relay did (#961, #966).</summary>
public class RelayDroppedHintTests : IDisposable
{
    private const string Tv = "203.0.113.20";
    private const string Reset = "Unable to read data from the transport connection: Connection reset by peer.";

    private readonly Func<string, CancellationToken, Task<string?>> _original = SdbEndpoint.ReadRelayReport;

    public RelayDroppedHintTests()
    {
        SdbEndpoint.Remember(Tv, "127.0.0.1");
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult<string?>(null);
    }

    public void Dispose() => SdbEndpoint.ReadRelayReport = _original;

    private static ProcessResult Failed(string error) => new() { ExitCode = 1, Output = error, Error = error };

    private static string Report(string code, string reason, DateTimeOffset at, string? client = null) =>
        new JsonObject
        {
            ["ok"] = true,
            ["relay"] = new JsonObject
            {
                ["enabled"] = true,
                ["lastFailure"] = new JsonObject { ["at"] = at.ToString("o"), ["client"] = client, ["code"] = code, ["reason"] = reason },
            },
        }.ToJsonString();

    [Fact]
    public async Task AResetThroughTheRelayGetsTheGenericHintWhenTheRelayHasNoReason()
    {
        var result = await InProcessSdbEngine.WithRelayHintAsync(Tv, Failed(Reset));

        Assert.Contains("Connection reset by peer", result.Error);
        Assert.Contains(SdbEndpoint.RelayDroppedHint(Tv, SdbEndpoint.RelayPort(Tv)), result.Error);
        Assert.Contains("Connect from Apps2Samsung", result.Output);
    }

    [Fact]
    public async Task TheRelaysOwnReasonReplacesTheGuess()
    {
        const string reason = "the TV's sdbd took the connection and closed it without answering: Developer Mode's Host PC IP is not 127.0.0.1";
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult<string?>(Report("SDBD_DROPPED", reason, DateTimeOffset.UtcNow));

        var result = await InProcessSdbEngine.WithRelayHintAsync(Tv, Failed(Reset));

        Assert.Contains($"TV-Store's relay reports: {reason}", result.Error);
        Assert.DoesNotContain("turn \"Connect from Apps2Samsung\" off and on", result.Error);
    }

    [Fact]
    public async Task ADeviceNotOnTheAllowListIsToldSo()
    {
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult<string?>(Report("NOT_ALLOWED", "this device is not allowed", DateTimeOffset.UtcNow));

        var result = await InProcessSdbEngine.WithRelayHintAsync(Tv, Failed(Reset));

        Assert.Contains("allow list", result.Error);
        Assert.Contains(SdbEndpoint.RelayHintMarker, result.Error);
    }

    [Fact]
    public async Task AnOldFailureOrAnotherDevicesFailureFallsBackToTheGenericHint()
    {
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult<string?>(Report("SDBD_DROPPED", "old news", DateTimeOffset.UtcNow.AddMinutes(-10)));
        var old = await InProcessSdbEngine.WithRelayHintAsync(Tv, Failed(Reset));
        Assert.DoesNotContain("old news", old.Error);
        Assert.Contains("closed the connection", old.Error);

        var failure = new SdbEndpoint.RelayFailure("SDBD_DROPPED", "someone else's", DateTimeOffset.UtcNow, "192.168.9.9");
        Assert.Null(SdbEndpoint.DescribeRelayFailure(failure, Tv, DateTimeOffset.UtcNow, new[] { "192.168.1.20" }));
        Assert.NotNull(SdbEndpoint.DescribeRelayFailure(failure with { Client = "192.168.1.20" }, Tv, DateTimeOffset.UtcNow, new[] { "192.168.1.20" }));
        Assert.NotNull(SdbEndpoint.DescribeRelayFailure(failure, Tv, DateTimeOffset.UtcNow, Array.Empty<string>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>404</html>")]
    [InlineData("{\"ok\":true,\"relay\":{\"enabled\":true,\"lastFailure\":null}}")]
    public void ABodyWithoutAFailureParsesToNothing(string? body)
    {
        Assert.Null(SdbEndpoint.ParseRelayFailure(body));
    }

    [Fact]
    public async Task AReaderThatThrowsStillGivesTheGenericHint()
    {
        SdbEndpoint.ReadRelayReport = (_, _) => throw new InvalidOperationException("boom");

        var result = await InProcessSdbEngine.WithRelayHintAsync(Tv, Failed(Reset));

        Assert.Contains(SdbEndpoint.RelayDroppedHint(Tv, SdbEndpoint.RelayPort(Tv)), result.Error);
    }

    [Fact]
    public async Task AFailureThatAlreadyCarriesARelayHintIsLeftAlone()
    {
        var down = Failed("Connection reset by peer. " + SdbEndpoint.RelayDownHint(Tv, 26201));

        Assert.Same(down, await InProcessSdbEngine.WithRelayHintAsync(Tv, down));
    }

    [Fact]
    public async Task ADirectTvOrANonTransportFailureIsLeftAlone()
    {
        SdbEndpoint.Remember(Tv, "192.168.1.20");
        var reset = Failed("Connection reset by peer");
        Assert.Same(reset, await InProcessSdbEngine.WithRelayHintAsync(Tv, reset));

        SdbEndpoint.Remember(Tv, "127.0.0.1");
        var refused = Failed("install failed[118, -22]");
        Assert.Same(refused, await InProcessSdbEngine.WithRelayHintAsync(Tv, refused));
    }
}
