using System.Text.Json.Nodes;
using Apps2Samsung.Diagnostics;

namespace Apps2Samsung.Tests.Diagnostics;

/// <summary>
/// An inspector replays the messages it kept to every client that enables its domains. A console
/// that reattaches to a page after its tunnel was handed over (#1140) must not print them twice.
/// </summary>
public class DevToolsConsoleReplayTests
{
    private static JsonNode ConsoleCall(double timestamp) => new JsonObject { ["type"] = "log", ["timestamp"] = timestamp };

    private static JsonNode LogEntry(double timestamp) => new JsonObject { ["entry"] = new JsonObject { ["timestamp"] = timestamp } };

    [Fact]
    public async Task TheMarkFollowsTheNewestEntryPerDomain()
    {
        await using var console = new DevToolsConsole();

        Assert.False(console.IsReplay("Runtime.consoleAPICalled", ConsoleCall(1000)));
        Assert.False(console.IsReplay("Runtime.exceptionThrown", ConsoleCall(1200)));
        Assert.False(console.IsReplay("Runtime.consoleAPICalled", ConsoleCall(1100)));
        Assert.False(console.IsReplay("Log.entryAdded", LogEntry(40)));

        Assert.Equal(new ConsoleReplayMark(1200, 40), console.Mark);
    }

    [Fact]
    public async Task EntriesUpToTheMarkAreSkippedAndNewerOnesArrive()
    {
        await using var console = new DevToolsConsole { SkipThrough = new ConsoleReplayMark(1200, 40) };

        Assert.True(console.IsReplay("Runtime.consoleAPICalled", ConsoleCall(1000)));
        Assert.True(console.IsReplay("Runtime.consoleAPICalled", ConsoleCall(1200)));
        Assert.True(console.IsReplay("Log.entryAdded", LogEntry(40)));

        // Logged while nothing was attached: newer than the mark, so it is shown.
        Assert.False(console.IsReplay("Runtime.consoleAPICalled", ConsoleCall(1201)));
        Assert.False(console.IsReplay("Log.entryAdded", LogEntry(41)));
    }

    [Fact]
    public async Task AnEntryWithoutATimestampIsNeverSkipped()
    {
        await using var console = new DevToolsConsole { SkipThrough = new ConsoleReplayMark(1200, 40) };

        Assert.False(console.IsReplay("Runtime.consoleAPICalled", new JsonObject { ["type"] = "log" }));
        Assert.False(console.IsReplay("Log.entryAdded", new JsonObject { ["entry"] = new JsonObject() }));
    }
}
