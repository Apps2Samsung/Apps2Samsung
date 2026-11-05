using System.Text.Json.Nodes;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Sdb;
using Apps2Samsung.TvStore;

namespace Apps2Samsung.Tests.TvStore;

/// <summary>
/// Updating TV-Store through TV-Store's own relay drops the head's connection once and still goes
/// through (#1005): the head knows the package and the port up front, takes the drop as expected,
/// and reads the new version without the developer connection.
/// </summary>
public class TvStoreSelfUpdateTests
{
    private const string Renames = """
        [{"TvDuid":"DUID1","OriginalPackageId":"A2STVStore","NewPackageId":"KUW3HHjMUR","OriginalAppId":"A2STVStore.Store","NewAppId":"KUW3HHjMUR.Store","PackageFile":"TV-Store.wgt","RenamedAtUtc":"2026-10-27T10:00:00+00:00"}]
        """;

    [Theory]
    [InlineData("A2STVStore", "A2STVStore.Store", true)]
    [InlineData("a2stvstore", null, true)]
    [InlineData("KUW3HHjMUR", "KUW3HHjMUR.Store", true)]
    [InlineData("Xyz", "Xyz.Store", false)]
    [InlineData("AprZAARz4r", "AprZAARz4r.Jellyfin", false)]
    [InlineData(null, null, false)]
    public void TheIdsSayWhetherThePackageIsTvStore(string? packageId, string? appId, bool expected)
    {
        Assert.Equal(expected, TvStoreSelfUpdate.IsTvStore(packageId, appId, Renames));
    }

    [Fact]
    public void ARenamedCopyIsOnlyTvStoreWithTheRecordThatSaysSo()
    {
        Assert.False(TvStoreSelfUpdate.IsTvStore("KUW3HHjMUR", "KUW3HHjMUR.Store", null));
    }

    [Theory]
    [InlineData(1, "Unable to read data from the transport connection: Connection reset by peer.", true)]
    [InlineData(1, "installing[40]\nRemote closed stream while reading", true)]
    [InlineData(0, "", true)]
    [InlineData(0, "installing[9]\ninstalling[40]", true)]
    [InlineData(0, "installing[100]\ninstall completed", false)]
    [InlineData(1, "app_id[A2STVStore.Store] install failed[118, -12]", false)]
    [InlineData(1, "val[fail] Connection reset by peer", false)]
    [InlineData(1, "The TV said no.", false)]
    public void OnlyADeadLinkWithoutAVerdictIsTheExpectedDrop(int exitCode, string output, bool expected)
    {
        var result = new ProcessResult { ExitCode = exitCode, Output = output, Error = exitCode == 0 ? "" : output };
        Assert.Equal(expected, TvStoreSelfUpdate.IsExpectedDrop(result));
    }

    [Fact]
    public void NothingIsNotADrop()
    {
        Assert.False(TvStoreSelfUpdate.IsExpectedDrop(null));
    }

    [Fact]
    public void TheNotBackMessageNamesTheVersionTheTvStillLists()
    {
        Assert.Contains("still lists version 0.3.19", TvStoreSelfUpdate.NotBackMessage(new TvStoreRestart(false, "0.3.19", true)));
        Assert.DoesNotContain("still lists", TvStoreSelfUpdate.NotBackMessage(new TvStoreRestart(false, null, false)));
        Assert.Contains("install completed", TvStoreSelfUpdate.CompletedOutput(new TvStoreRestart(true, "0.3.20", true)));
    }
}

/// <summary>The wait after the drop, against a TV that answers in steps.</summary>
public sealed class TvStoreRestartWaitTests : IDisposable
{
    private const string Tv = "192.168.1.60";
    private const string AppId = "A2STVStore.Store";

    private readonly Func<string, string, CancellationToken, Task<string?>> _readVersion = TvStoreSelfUpdate.ReadVersion;
    private readonly Func<string, string, CancellationToken, Task<bool>> _launch = TvStoreSelfUpdate.Launch;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = TvStoreSelfUpdate.Delay;
    private readonly Func<string, CancellationToken, Task<string?>> _relay = SdbEndpoint.ReadRelayReport;
    private readonly Func<TimeSpan, CancellationToken, Task> _relayDelay = SdbEndpoint.Delay;
    private readonly Func<DateTime> _clock = TvStoreSelfUpdate.Clock;
    private readonly Func<DateTime> _relayClock = SdbEndpoint.Clock;

    private readonly List<string> _progress = new();
    private int _launches;

    // A clock that only the waits advance, so the deadlines hold without real seconds passing.
    private DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public TvStoreRestartWaitTests()
    {
        TvStoreSelfUpdate.Clock = () => _now;
        SdbEndpoint.Clock = () => _now;
        TvStoreSelfUpdate.Delay = (wait, _) => { _now += wait; return Task.CompletedTask; };
        SdbEndpoint.Delay = (wait, _) => { _now += wait; return Task.CompletedTask; };
        TvStoreSelfUpdate.Launch = (_, _, _) => { _launches++; return Task.FromResult(true); };
    }

    public void Dispose()
    {
        TvStoreSelfUpdate.ReadVersion = _readVersion;
        TvStoreSelfUpdate.Launch = _launch;
        TvStoreSelfUpdate.Delay = _delay;
        TvStoreSelfUpdate.Clock = _clock;
        SdbEndpoint.ReadRelayReport = _relay;
        SdbEndpoint.Delay = _relayDelay;
        SdbEndpoint.Clock = _relayClock;
    }

    private static string Relay(bool enabled, bool listening) =>
        new JsonObject { ["ok"] = true, ["relay"] = new JsonObject { ["enabled"] = enabled, ["listening"] = listening, ["port"] = 26201 } }.ToJsonString();

    private static Func<string, string, CancellationToken, Task<string?>> Versions(params string?[] answers)
    {
        var i = 0;
        return (_, _, _) => Task.FromResult(answers[Math.Min(i++, answers.Length - 1)]);
    }

    private static Func<string, CancellationToken, Task<string?>> Relays(params string?[] answers)
    {
        var i = 0;
        return (_, _) => Task.FromResult(answers[Math.Min(i++, answers.Length - 1)]);
    }

    [Fact]
    public async Task TheNewVersionThenTheRelayMeansTvStoreIsBack()
    {
        TvStoreSelfUpdate.ReadVersion = Versions(null, "0.3.19", "0.3.20");
        SdbEndpoint.ReadRelayReport = Relays(null, Relay(true, false), Relay(true, true));

        var restart = await TvStoreSelfUpdate.AwaitRestartAsync(Tv, AppId, "0.3.20", null, _progress.Add);

        Assert.True(restart.CameBack);
        Assert.Equal("0.3.20", restart.Version);
        Assert.True(restart.RelayListening);
        Assert.Equal(1, _launches);
        Assert.Equal(new[] { "statusTvStoreRestarting", "statusOpeningTvStore", "statusWaitingForRelay" }, _progress);
    }

    [Fact]
    public async Task AnyVersionCountsWhenThePackageNamedNone()
    {
        TvStoreSelfUpdate.ReadVersion = Versions("0.3.20");
        SdbEndpoint.ReadRelayReport = Relays(Relay(true, true));

        var restart = await TvStoreSelfUpdate.AwaitRestartAsync(Tv, AppId, null, null);

        Assert.True(restart.CameBack);
    }

    [Fact]
    public async Task TheOldVersionStillListedIsNotBack()
    {
        TvStoreSelfUpdate.ReadVersion = Versions("0.3.19");
        SdbEndpoint.ReadRelayReport = Relays(Relay(true, true));

        var restart = await TvStoreSelfUpdate.AwaitRestartAsync(Tv, AppId, "0.3.20", null);

        Assert.False(restart.CameBack);
        Assert.Equal("0.3.19", restart.Version);
        Assert.Contains("0.3.19", TvStoreSelfUpdate.NotBackMessage(restart));
    }

    [Fact]
    public async Task NoAnswerAtAllIsNotBack()
    {
        TvStoreSelfUpdate.ReadVersion = Versions((string?)null);
        SdbEndpoint.ReadRelayReport = Relays((string?)null);

        var restart = await TvStoreSelfUpdate.AwaitRestartAsync(Tv, AppId, "0.3.20", null);

        Assert.False(restart.CameBack);
        Assert.Null(restart.Version);
        Assert.False(restart.RelayListening);
        // Launched anyway: the relay is the user's way in, and a TV-Store that is there comes up.
        Assert.Equal(1, _launches);
    }
}

/// <summary>The relay's <c>listening</c> flag, and waiting for it (#1005).</summary>
public sealed class RelayListeningTests : IDisposable
{
    private const string Tv = "192.168.1.61";
    private readonly Func<string, CancellationToken, Task<string?>> _relay = SdbEndpoint.ReadRelayReport;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = SdbEndpoint.Delay;
    private readonly Func<DateTime> _clock = SdbEndpoint.Clock;
    private DateTime _now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public RelayListeningTests()
    {
        SdbEndpoint.Clock = () => _now;
        SdbEndpoint.Delay = (wait, _) => { _now += wait; return Task.CompletedTask; };
    }

    public void Dispose()
    {
        SdbEndpoint.ReadRelayReport = _relay;
        SdbEndpoint.Delay = _delay;
        SdbEndpoint.Clock = _clock;
    }

    private static string Relay(bool enabled, bool listening) =>
        new JsonObject { ["ok"] = true, ["relay"] = new JsonObject { ["enabled"] = enabled, ["listening"] = listening } }.ToJsonString();

    [Fact]
    public void ListeningIsReadAsTheRelaySaysIt()
    {
        Assert.True(SdbEndpoint.ParseRelayListening(Relay(true, true)));
        Assert.False(SdbEndpoint.ParseRelayListening(Relay(true, false)));
        Assert.Null(SdbEndpoint.ParseRelayListening(null));
        Assert.Null(SdbEndpoint.ParseRelayListening("not json"));
        Assert.Null(SdbEndpoint.ParseRelayListening("""{"ok":true}"""));
    }

    [Fact]
    public async Task AListeningRelayEndsTheWaitAtOnce()
    {
        var reads = 0;
        SdbEndpoint.ReadRelayReport = (_, _) => { reads++; return Task.FromResult<string?>(Relay(true, true)); };
        Assert.True(await SdbEndpoint.WaitForRelayAsync(Tv, TimeSpan.FromSeconds(10), untilReachable: true));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ARelayStillStartingIsWaitedFor()
    {
        var answers = new Queue<string?>(new[] { null, Relay(true, false), Relay(true, true) });
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult(answers.Count > 0 ? answers.Dequeue() : Relay(true, true));
        Assert.True(await SdbEndpoint.WaitForRelayAsync(Tv, TimeSpan.FromSeconds(10), untilReachable: true));
        Assert.Empty(answers);
    }

    [Fact]
    public async Task ARelaySwitchedOffIsNotWaitedFor()
    {
        var reads = 0;
        SdbEndpoint.ReadRelayReport = (_, _) => { reads++; return Task.FromResult<string?>(Relay(false, false)); };
        Assert.False(await SdbEndpoint.WaitForRelayAsync(Tv, TimeSpan.FromSeconds(10), untilReachable: true));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ATvStoreThatCannotSayIsNotHeldUpBeforeAConnect()
    {
        var reads = 0;
        SdbEndpoint.ReadRelayReport = (_, _) => { reads++; return Task.FromResult<string?>(null); };
        Assert.True(await SdbEndpoint.WaitForRelayAsync(Tv, TimeSpan.FromSeconds(10), untilReachable: false));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task TheWaitGivesUpAtTheDeadline()
    {
        SdbEndpoint.ReadRelayReport = (_, _) => Task.FromResult<string?>(null);
        Assert.False(await SdbEndpoint.WaitForRelayAsync(Tv, TimeSpan.Zero, untilReachable: true));
    }
}
